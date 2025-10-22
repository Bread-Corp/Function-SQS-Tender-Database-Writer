# 🗄️ Database Writer Lambda — Tender Persistence Service

This is the **final microservice** in the tender processing data pipeline. It consumes fully processed, summarized, and tagged tender messages from the `WriteQueue`, transforms this data from SQS message format into Entity Framework database entities, and writes them to the primary MS SQL Server (RDS) database.

Its sole responsibility is to ensure data integrity by handling the complex many-to-many tag relationship, calculating the tender's final `Status`, and routing any messages that fail the write operation to a dedicated `DBWriteFailedQueue`.

## 📚 Table of Contents

- [✨ Key Features](#-key-features)
- [🧭 Architecture & Data Flow](#-architecture--data-flow)
- [🧠 How It Works: The Database Write Pipeline](#-how-it-works-the-database-write-pipeline)
- [🧩 Project Structure](#-project-structure)
- [⚙️ Configuration (Critical)](#️-configuration-critical)
- [🔒 IAM Permissions](#-iam-permissions)
- [📦 Tech Stack](#-tech-stack)
- [🚀 Getting Started](#-getting-started)
- [📦 Deployment Guide](#-deployment-guide)
- [🧰 Troubleshooting & Team Gotchas](#-troubleshooting--team-gotchas)
- [🗺️ Roadmap](#️-roadmap)

## ✨ Key Features

- **🛡️ Final Persistence Layer**: The last step in the pipeline, saving all enriched tender data to the SQL Server database.

- **🔄 Model Transformation**: Intelligently maps queue-based "Input" models (e.g., `Sqs_Tagging_Lambda.Models.SanralTenderMessage`) to the database's "Output" entity models (e.g., `Tender_Core_Logic.Models.SanralTender`).

- **🔗 Smart Tag Handling**: Manages the many-to-many `Tag` relationship. It efficiently queries the DB to find existing tags and creates new ones *within the same transaction* to prevent duplicates.

- **⏱️ Dynamic Status Calculation**: Automatically sets the tender's `Status` field to "Open" or "Closed" by comparing its `ClosingDate` to the current time (`DateTime.UtcNow`).

- **🗄️ Robust DB Transactions**: Uses `DbContextFactory` to ensure a clean, scoped database context for every write operation, preventing state conflicts.

- **🔁 Resilient Error Handling**: Follows a transactional, batch-processing pattern. Any message that fails the database write (due to a SQL error, constraint violation, etc.) is caught and routed to the `DBWriteFailedQueue` for manual inspection, preventing data loss and not blocking the pipeline.

## 🧭 Architecture & Data Flow

This function is the final destination in the data processing flow.

```
AI Tagging Lambda (Sqs_Tagging_Lambda)
    ↓
WriteQueue (WriteQueue.fifo) ← Fully enriched tenders with tags
    ↓
Database Writer Lambda (Sqs_Database_Writer)
    ├─ Message Factory (deserialize to Input models)
    ├─ Tender Writer Service
    │   ├─ Resolve Tags (existing vs new tag entities)
    │   ├─ Map Models (Input → Output entities)
    │   ├─ Calculate Status (Open/Closed based on ClosingDate)
    │   └─ DB Context Factory → Entity Framework Core
    └─ SQS Service (I/O)
           ├─ Success → Delete from WriteQueue
           └─ DBWriteFailedQueue (DBWriteFailedQueue.fifo) ← SQL errors/DLQ
                ↓
Amazon RDS (MS SQL Server Database)
    ├─ BaseTender table
    ├─ Source-specific tables (SanralTender, SarsTender, etc.)
    ├─ Tag table
    └─ BaseTenderTag (many-to-many join table)
```

## 🧠 How It Works: The Database Write Pipeline

This function executes a specific sequence for every tender message it processes.

1. **Ingest & Deserialize**: The `FunctionHandler` receives a batch of messages from `WriteQueue.fifo`. The `MessageFactory` (reused from upstream services) deserializes each JSON body into its specific *Input* model (e.g., `Input.SanralTenderMessage`).

2. **Create Context**: For each message, a new `ITenderWriterService` is created, which in turn gets a fresh `ApplicationDbContext` from the `DbContextFactory`. This ensures each write is an isolated, transactional unit.

3. **Resolve Tags (The Core Logic)**: The `TenderWriterService` calls a private `ResolveTagsAsync` helper method. This method:
   - Takes the `List<string>` of tag names from the message.
   - Runs **one** efficient query against the `Tags` table to find all tags that *already exist* in the database.
   - It loops through the original tag names. If a tag exists, it adds the tracked EF entity to a final list. If a tag is new, it creates a `new Output.Tag()` object, adds it to the `DbContext` (to be inserted), and adds it to the final list.
   - It returns a `List<Output.Tag>` (database entities) that are a mix of existing and newly created tags.

4. **Map Models**: The service calls a `MapToDbEntity` helper. This:
   - Uses a `switch` statement on the message's `GetSourceType()` to create the correct "Output" (database) entity (e.g., `new Output.SarsTender()`).
   - Calls a `MapBaseFields` helper to populate all common properties (`Title`, `Description`, `AISummary`, `Source`, etc.).
   - Sets new required fields: `TenderID = Guid.NewGuid()`, `DateAppended = DateTime.UtcNow`.
   - Calculates the `Status` ("Open" or "Closed") by comparing the `ClosingDate` to `DateTime.UtcNow`.
   - Assigns the `List<Output.Tag>` from Step 3 to the `dbTender.Tags` property.

5. **Save Changes (Transaction)**:
   - `context.Tenders.Add(dbTender)` is called.
   - `await context.SaveChangesAsync()` is called.
   - Entity Framework Core is smart enough to see the new `BaseTender`, the new child `SarsTender`, the new `Tag` objects, and the relationship between them. It generates all the necessary SQL `INSERT` statements for `BaseTender`, `SarsTender`, `Tag`, and the `BaseTenderTag` join table—all within a single, protected transaction.

6. **Route & Cleanup**:
   - If the `try` block succeeds, the message's receipt handle is added to a "delete list".
   - If the `try` block fails (e.g., SQL connection error), the message and exception are sent to the `DBWriteFailedQueue`, and the receipt handle is *also* added to the "delete list" (to prevent retrying a "poison pill" message).
   - Finally, `DeleteMessageBatchAsync` is called to remove all processed (success or fail) messages from the `WriteQueue.fifo`.

## 🧩 Project Structure

This function is structured to clearly separate incoming data models from database entity models.

```
Sqs_Database_Writer/
├── Function.cs                 # Lambda entry point, DI setup, polling loop
├── Data/
│   └── ApplicationDbContext.cs   # (Copied) EF Core database context
├── Models/
│   ├── Input/                  # (Copied) SQS Message Models
│   │   ├── TenderMessageBase.cs
│   │   ├── SanralTenderMessage.cs
│   │   ├── QueueMessage.cs
│   │   └── ... (etc.)
│   └── Output/                 # (Copied) Database Entity Models
│       ├── BaseTender.cs
│       ├── SanralTender.cs
│       ├── Tag.cs
│       └── ... (etc.)
├── Services/
│   ├── TenderWriterService.cs  # New! Core logic for mapping and writing
│   ├── MessageFactory.cs       # (Reused) Deserializes JSON to Input models
│   └── SqsService.cs           # (Reused) SQS send/delete operations
├── Interfaces/
│   ├── ITenderWriterService.cs # New!
│   ├── IMessageFactory.cs      # (Reused)
│   └── ISqsService.cs          # (Reused)
├── aws-lambda-tools-defaults.json # Deployment config
└── README.md
```

## ⚙️ Configuration (Critical)

This function will not run without the following resources being correctly configured.

### 1. SQS Queues

- **Source**: `WriteQueue.fifo` (Must exist)
- **Failure**: `DBWriteFailedQueue.fifo` (Must be created)

### 2. AWS RDS

- A running MS SQL Server RDS instance, accessible from within the Lambda's VPC.

### 3. Lambda Environment Variables (3 Required)

| Variable Name | Required | Description |
|---------------|----------|-------------|
| `SOURCE_QUEUE_URL` | **Yes** | The full URL of the `WriteQueue.fifo`. |
| `FAILED_QUEUE_URL` | **Yes** | The full URL of the `DBWriteFailedQueue.fifo`. |
| `DB_CONNECTION_STRING` | **Yes** | The full SQL Server connection string for your RDS database. |

## 🔒 IAM Permissions

Your Lambda's execution role **must** have the following permissions:

1. **SQS (Read/Delete)**: `sqs:ReceiveMessage`, `sqs:DeleteMessage`, `sqs:GetQueueAttributes` on `WriteQueue.fifo`.

2. **SQS (Send)**: `sqs:SendMessage` on `DBWriteFailedQueue.fifo`.

3. **CloudWatch Logs**: `logs:CreateLogGroup`, `logs:CreateLogStream`, `logs:PutLogEvents`.

4. **VPC Access (CRITICAL)**:
   - `ec2:CreateNetworkInterface`
   - `ec2:DescribeNetworkInterfaces`
   - `ec2:DeleteNetworkInterface`

   > These permissions are required for the Lambda to connect to your VPC and access both the RDS database and the SQS VPC Endpoint.

## 📦 Tech Stack

- **Runtime**: .NET 8 (LTS)
- **Compute**: AWS Lambda
- **Database**: MS SQL Server on Amazon RDS
- **Data Access**: Entity Framework Core
- **Messaging**: Amazon SQS (FIFO)
- **Serialization**: System.Text.Json, Newtonsoft.Json
- **Logging/DI**: Microsoft.Extensions.*
- **AWS SDKs**:
  - AWSSDK.SQS
  - Microsoft.EntityFrameworkCore.SqlServer

## 🚀 Getting Started

Follow these steps to set up the project for local development.

### Prerequisites

- .NET 8 SDK
- AWS CLI configured
- Access to the SQL Server RDS instance

### Local Setup

1. **Clone the repository:**
   ```bash
   git clone <your-repository-url>
   cd Sqs_Database_Writer
   ```

2. **Restore Dependencies:**
   ```bash
   dotnet restore
   ```

3. **Configure User Secrets:**
   ```bash
   dotnet user-secrets init
   dotnet user-secrets set "SOURCE_QUEUE_URL" "your-write-queue-url"
   dotnet user-secrets set "FAILED_QUEUE_URL" "your-db-failed-queue-url"
   dotnet user-secrets set "DB_CONNECTION_STRING" "your-full-connection-string"
   ```

## 📦 Deployment Guide

### Step 1: Build & Package

```bash
dotnet lambda package -c Release -o ./build/deploy-package.zip
```

### Step 2: Deploy to Lambda

1. Create a new Lambda function (`Sqs_Database_Writer`) with a .NET 8 runtime and your IAM role.
2. Upload the `deploy-package.zip` file.
3. **Handler**: Set to `Sqs_Database_Writer::Sqs_Database_Writer.Function::FunctionHandler`.
4. **VPC**: Connect the Lambda to the **same VPC and private subnets** as your RDS database.
5. **Security Group**: Assign a security group that can access your RDS instance (e.g., has an inbound rule allowing port `1433` from this SG) AND the SQS VPC Endpoint (has an inbound rule allowing port `443` from this SG).
6. **Environment Variables**: Add the 3 required variables from the Configuration section.
7. **Timeout**: Set a reasonable timeout (e.g., **2-3 minutes**) to allow for database connections and polling.
8. **Trigger**: Add an SQS trigger pointing to `WriteQueue.fifo` with a batch size of 10.

## 🧰 Troubleshooting & Team Gotchas

<details>
<summary><strong>ERROR: Connection timed out (sqs.us-east-1.amazonaws.com:443)</strong></summary>

**Issue**: The function successfully writes to the database but then hangs and times out when trying to delete the message from the `WriteQueue`.

**Root Cause**: The function is in a VPC to access RDS, which means it loses its default route to the public internet. The SQS API (`sqs.us-east-1...`) is a public endpoint.

**Fix**: Your VPC must have an **SQS VPC Endpoint**. This error means the Lambda's Security Group is not allowed to make inbound connections to the **Endpoint's Security Group** on port `443` (HTTPS). Add an inbound rule to the *Endpoint's* security group allowing `HTTPS` (443) from the *Lambda's* security group.

</details>

<details>
<summary><strong>ERROR: A network-related or instance-specific error occurred...</strong></summary>

**Issue**: A `SqlException` occurs when the function tries to save changes.

**Root Cause**: This is a database connection failure. The Lambda function cannot reach the RDS instance.

**Fix**:
1. **VPC**: Ensure the Lambda and RDS instance are in the **same VPC**.
2. **Subnets**: Ensure the Lambda is assigned to the **private subnets**.
3. **Security Group**: This is the most common cause.
   - Go to the Security Group for your **RDS instance**.
   - Edit its **Inbound Rules**.
   - Add a rule:
     - **Type**: `MS SQL`
     - **Port**: `1433`
     - **Source**: The Security Group ID of your **Lambda function** (e.g., `sg-xxxxxxxx`).

</details>

<details>
<summary><strong>ERROR: InvalidOperationException: Message factory returned null</strong></summary>

**Issue**: The function fails immediately on deserialization.

**Root Cause**: The `MessageGroupId` on the SQS message (e.g., "SANRAL") does not have a matching `case` in the `MessageFactory.cs` `switch` statement.

**Fix**: Open `Services/MessageFactory.cs` and add a new entry to the `switch` statement for the missing `MessageGroupId` (e.g., `case "newsource"`).

</details>

<details>
<summary><strong>ERROR: DbContext Concurrency Exception (A second operation was started...)</strong></summary>

**Issue**: Multiple parallel operations attempting to use the same DbContext simultaneously.

**Root Cause**: The DbContext was registered as a singleton, but multiple parallel operations were trying to use it.

**Fix**: Ensure you're using `services.AddDbContextFactory(...)` instead of `services.AddDbContext(...)` in the DI registration. Inject `IDbContextFactory` and create a new context for each operation.

</details>

## 🗺️ Roadmap

- **Batch Writing**: Refactor `TenderWriterService` to process the *entire batch* in a single `DbContext` and one `SaveChangesAsync()` call, rather than one-by-one. This will be significantly faster and more cost-effective.

- **"Upsert" Logic**: Add logic to the `TenderWriterService` to *check if a tender already exists* (based on `TenderNumber` and `Source`) and perform an **update (upsert)** instead of an insert. This will make the function idempotent and prevent duplicates if messages are ever re-driven.

- **Dead-Letter Queue (DLQ) Automation**: Create a system to alert the team when messages land in the `DBWriteFailedQueue`.

- **Performance Monitoring**: Implement CloudWatch metrics to track database write performance and success rates.

---

> Built with love, bread, and code by **Bread Corporation** 🦆❤️💻