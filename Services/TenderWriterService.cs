using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenderDatabaseWriterLambda.Data;
using TenderDatabaseWriterLambda.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

// Create aliases for Input (Queue) and Output (DB) models to avoid name conflicts
using Input = TenderDatabaseWriterLambda.Models.Input;
using Output = TenderDatabaseWriterLambda.Models.Output;

namespace TenderDatabaseWriterLambda.Services
{
    /// <summary>
    /// Implements the logic for transforming queue messages into database entities
    /// and writing them to the SQL Server database based on the final, fixed schema.
    /// </summary>
    public class TenderWriterService : ITenderWriterService
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
        private readonly ILogger<TenderWriterService> _logger;

        public TenderWriterService(IDbContextFactory<ApplicationDbContext> contextFactory, ILogger<TenderWriterService> logger)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        /// <inheritdoc/>
        public async Task WriteTenderAsync(Input.TenderMessageBase queueMessage)
        {
            // Create a new DbContext for this specific, isolated write operation
            await using var context = await _contextFactory.CreateDbContextAsync();

            _logger.LogInformation("Writing tender {TenderNumber} from source {Source} to database.", queueMessage.TenderNumber, queueMessage.GetSourceType());

            // --- 1. Resolve Tag Relationships ---
            // This handles the many-to-many relationship defined in your DbContext
            var dbTags = await ResolveTagsAsync(context, queueMessage.Tags);

            // --- 2. Map Queue Model to DB Entity ---
            // This maps all fields according to your new business rules
            var dbTender = MapToDbEntity(queueMessage, dbTags);

            // --- 3. Add and Save ---
            // EF Core will now, in a single transaction, insert into:
            // BaseTender, SarsTender (or other child), Tag (if new), BaseTenderTag, and SupportingDoc
            context.Tenders.Add(dbTender);
            await context.SaveChangesAsync();

            _logger.LogInformation("Successfully wrote new tender {TenderID} to database.", dbTender.TenderID);
        }

        /// <summary>
        /// Efficiently finds existing tags or creates new ones in the database.
        /// This method is required by your many-to-many schema.
        /// </summary>
        private async Task<List<Output.Tag>> ResolveTagsAsync(ApplicationDbContext context, List<string> tagNames)
        {
            var finalTags = new List<Output.Tag>();
            if (tagNames == null || !tagNames.Any())
            {
                return finalTags; // Return empty list if no tags
            }

            // 1. Find all tags that *already exist* in the DB in a single query
            var existingTags = await context.Tags
                .Where(t => tagNames.Contains(t.TagName))
                .ToDictionaryAsync(t => t.TagName, StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("Found {Count} existing tags in database from a list of {Total}", existingTags.Count, tagNames.Count);

            foreach (var tagName in tagNames)
            {
                if (existingTags.TryGetValue(tagName, out var existingTag))
                {
                    // 2. If tag exists, add the tracked DB entity
                    finalTags.Add(existingTag);
                }
                else
                {
                    // 3. If tag is new, create it, add it to the context, and add to our tracking dictionary
                    _logger.LogDebug("Creating new tag: {TagName}", tagName);
                    var newTag = new Output.Tag
                    {
                        TagID = Guid.NewGuid(),
                        TagName = tagName
                    };

                    context.Tags.Add(newTag); // Start tracking
                    finalTags.Add(newTag);
                    existingTags.Add(tagName, newTag); // Add to dictionary to prevent duplicates in this batch
                }
            }
            return finalTags;
        }

        /// <summary>
        /// Main mapping function that routes to the correct source-specific mapper.
        /// </summary>
        private Output.BaseTender MapToDbEntity(Input.TenderMessageBase qm, List<Output.Tag> dbTags)
        {
            // First, create the specific tender type (e.g., SarsTender)
            // This will populate the fields unique to that type based on your rules.
            Output.BaseTender dbTender = qm.GetSourceType().ToLowerInvariant() switch
            {
                "sars" => MapSarsTender(qm as Input.SarsTenderMessage, qm),
                "etenders" => MapETender(qm as Input.ETenderMessage, qm),
                "eskom" => MapEskomTender(qm as Input.EskomTenderMessage, qm),
                "sanral" => MapSanralTender(qm as Input.SanralTenderMessage, qm),
                "transnet" => MapTransnetTender(qm as Input.TransnetTenderMessage, qm),
                _ => throw new NotSupportedException($"Source type '{qm.GetSourceType()}' is not supported.")
            };

            // Next, apply all the common/base properties
            MapBaseFields(dbTender, qm, dbTags);
            return dbTender;
        }

        /// <summary>
        /// Maps all common fields from the input model to the output DB entity.
        /// </summary>
        private void MapBaseFields(Output.BaseTender dbTender, Input.TenderMessageBase qm, List<Output.Tag> dbTags)
        {
            dbTender.TenderID = Guid.NewGuid();
            dbTender.DateAppended = DateTime.UtcNow;
            dbTender.Title = qm.Title;
            dbTender.Description = qm.Description;
            dbTender.AISummary = qm.AISummary;
            dbTender.Source = qm.GetSourceType();

            // Assign the resolved list of DB tags (many-to-many relationship)
            dbTender.Tags = dbTags;

            // Map supporting documents (one-to-many relationship)
            var inputDocs = GetSupportingDocs(qm);
            if (inputDocs != null)
            {
                dbTender.SupportingDocs = inputDocs.Select(doc => new Output.SupportingDoc
                {
                    SupportingDocID = Guid.NewGuid(),
                    Name = doc.Name,
                    URL = doc.Url
                    // TenderID and Tender properties are set automatically by EF Core
                }).ToList();
            }
        }

        /// <summary>
        /// Gets the correct supporting documents list based on tender type
        /// (since eTender and Transnet use a different property name in the JSON)
        /// </summary>
        private List<Input.SupportingDocument> GetSupportingDocs(Input.TenderMessageBase tender)
        {
            return tender switch
            {
                Input.ETenderMessage eTender => eTender.SupportingDocs,
                Input.TransnetTenderMessage transnetTender => transnetTender.SupportingDocs,
                _ => tender.SupportingDocs
            };
        }

        /// <summary>
        /// Helper to dynamically calculate the tender status.
        /// </summary>
        private string CalculateStatus(DateTime? closingDate, string? eTenderStatus = null)
        {
            // eTenders provides its own status, which we trust first.
            if (!string.IsNullOrWhiteSpace(eTenderStatus))
            {
                return eTenderStatus;
            }
            // For all other tenders, calculate based on date.
            if (!closingDate.HasValue)
            {
                return "Open"; // Default to "Open" if no closing date is provided
            }
            return closingDate.Value > DateTime.UtcNow ? "Open" : "Closed";
        }

        // --- Source-Specific Mappers (Implementing Your New Rules) ---

        private Output.SarsTender MapSarsTender(Input.SarsTenderMessage? qm, Input.TenderMessageBase qmBase)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            var db = new Output.SarsTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                BriefingSession = qm.BriefingSession,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.Today,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue,
            };
            // Set dynamic status
            db.Status = CalculateStatus(qm.ClosingDate);
            return db;
        }

        private Output.eTender MapETender(Input.ETenderMessage? qm, Input.TenderMessageBase qmBase)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            var db = new Output.eTender
            {
                // Mapped fields per your rules
                TenderNumber = qmBase.TenderNumber, // Rule: TenderNumber = Id
                Audience = qmBase.Audience,     // Rule: Audience (DB) = Audience (Input)
                Email = qmBase.Email,           // Rule: Email (DB) = Email (Input)
                OfficeLocation = qmBase.OfficeLocation, // Rule: OfficeLocation (DB) = OfficeLocation (Input)
                Address = qmBase.Address,       // Rule: Address (DB) = Address (Input)
                Province = qmBase.Province,     // Rule: Province (DB) = Province (Input)

                // Base fields
                PublishedDate = qm.DatePublished,
                ClosingDate = qm.DateClosing,
            };
            // Set dynamic status (using eTender's specific status first)
            db.Status = CalculateStatus(qm.DateClosing, qm.Status);
            return db;
        }

        private Output.EskomTender MapEskomTender(Input.EskomTenderMessage? qm, Input.TenderMessageBase qmBase)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            var db = new Output.EskomTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,

                // Eskom "Anomaly" Rule: Map base input fields to derived output model
                Reference = qmBase.Reference,
                Audience = qmBase.Audience,
                OfficeLocation = qmBase.OfficeLocation,
                Email = qmBase.Email,
                Address = qmBase.Address,
                Province = qmBase.Province,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.Today,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue,
            };
            db.Status = CalculateStatus(qm.ClosingDate);
            return db;
        }

        private Output.SanralTender MapSanralTender(Input.SanralTenderMessage? qm, Input.TenderMessageBase qmBase)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            var db = new Output.SanralTender
            {
                TenderNumber = qm.TenderNumber,
                Category = qm.Category,
                FullTextNotice = qm.FullNoticeText,

                // Rule: Location (DB) = Region (Input)
                Location = qm.Region,

                // Mapping base fields to the derived model
                Email = qmBase.Email,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.Today,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue,
            };
            db.Status = CalculateStatus(qm.ClosingDate);
            return db;
        }

        private Output.TransnetTender MapTransnetTender(Input.TransnetTenderMessage? qm, Input.TenderMessageBase qmBase)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            var db = new Output.TransnetTender
            {
                TenderNumber = qm.TenderNumber,
                Category = qm.Category,
                Institution = qm.Institution,
                TenderType = qm.TenderType,

                // Rule: Region (DB) = Location (Input)
                Region = qm.Location,

                // Rule: Email (DB) = Email (Input)
                Email = qm.Email,

                // Rule: ContactPerson (DB) = ContactPerson (Input)
                ContactPerson = qm.ContactPerson,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.Today,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue,
            };
            // Set dynamic status
            db.Status = CalculateStatus(qm.ClosingDate);
            return db;
        }
    }
}
