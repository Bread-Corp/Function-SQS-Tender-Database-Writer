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
    /// and writing them to the SQL Server database.
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
        public async Task WriteTenderAsync(Input.TenderMessageBase queueMessage)
        {
            // Create a new DbContext for this specific write operation
            await using var context = await _contextFactory.CreateDbContextAsync();

            _logger.LogInformation("Writing tender {TenderNumber} from source {Source} to database.", queueMessage.TenderNumber, queueMessage.GetSourceType());

            // --- 1. Map Queue Model to DB Entity ---
            // The mapping process now handles tags and status internally.
            var dbTender = MapToDbEntity(queueMessage);

            // --- 2. Add and Save ---
            // Because Tags are part of the BaseTender model (owned relationship),
            // adding the dbTender will also add all the new Tag objects.
            context.Tenders.Add(dbTender);
            await context.SaveChangesAsync();

            _logger.LogInformation("Successfully wrote new tender {TenderID} to database.", dbTender.TenderID);
        }

        /// <summary>
        /// Main mapping function that routes to the correct source-specific mapper.
        /// </summary>
        private Output.BaseTender MapToDbEntity(Input.TenderMessageBase qm)
        {
            // First, create the specific tender type (e.g., SarsTender)
            // This will populate the fields unique to that type.
            Output.BaseTender dbTender = qm.GetSourceType().ToLowerInvariant() switch
            {
                "sars" => MapSarsTender(qm as Input.SarsTenderMessage),
                "etenders" => MapETender(qm as Input.ETenderMessage),
                "eskom" => MapEskomTender(qm as Input.EskomTenderMessage),
                "sanral" => MapSanralTender(qm as Input.SanralTenderMessage),
                "transnet" => MapTransnetTender(qm as Input.TransnetTenderMessage),
                _ => throw new NotSupportedException($"Source type '{qm.GetSourceType()}' is not supported.")
            };

            // Next, apply all the common/base properties
            MapBaseFields(dbTender, qm);
            return dbTender;
        }

        /// <summary>
        /// Maps all common fields from the input model to the output DB entity.
        /// </summary>
        private void MapBaseFields(Output.BaseTender dbTender, Input.TenderMessageBase qm)
        {
            dbTender.TenderID = Guid.NewGuid();
            dbTender.Title = qm.Title;
            dbTender.Description = qm.Description;
            dbTender.AISummary = qm.AISummary; // Map the summary
            dbTender.Source = qm.GetSourceType();
            dbTender.DateAppended = DateTime.UtcNow;

            // The ClosingDate was already set by the specific mapper (e.g., MapSarsTender)
            // A non-existent date was set to DateTime.MaxValue by the mapper.
            if (dbTender.ClosingDate == DateTime.MaxValue || dbTender.ClosingDate == DateTime.MinValue)
            {
                // Default to "Open" if date was null or invalid
                dbTender.Status = "Open";
            }
            else
            {
                // Compare the ClosingDate to the current time
                dbTender.Status = dbTender.ClosingDate > DateTime.UtcNow ? "Open" : "Closed";
            }

            // Convert the list of strings from the queue into a list of Tag objects for the database
            if (qm.Tags != null)
            {
                dbTender.Tags = qm.Tags.Select(tagName => new Output.Tag
                {
                    TagID = Guid.NewGuid(), // Create a new primary key for the Tag object
                    TagName = tagName
                }).ToList();
            }
            else
            {
                dbTender.Tags = new List<Output.Tag>(); // Ensure list is not null
            }

            // We intentionally skip SupportingDocs as it's [NotMapped] in the DB model
        }

        // --- Source-Specific Mappers ---
        // These now only set properties unique to their class

        private Output.SarsTender MapSarsTender(Input.SarsTenderMessage? qm)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            return new Output.SarsTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                BriefingSession = qm.BriefingSession,

                // Base fields (required by DB model)
                PublishedDate = qm.PublishedDate ?? DateTime.UtcNow,
                // Default null closing dates to MaxValue so our Status logic defaults to "Open"
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue
            };
        }

        private Output.eTender MapETender(Input.ETenderMessage? qm)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            return new Output.eTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                Category = null, // This field doesn't exist in the input model
                TenderType = null, // This field doesn't exist in the input model
                Department = null, // This field doesn't exist in the input model

                // Base fields (input model has non-nullable dates)
                PublishedDate = qm.DatePublished,
                ClosingDate = qm.DateClosing
            };
        }

        private Output.EskomTender MapEskomTender(Input.EskomTenderMessage? qm)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            return new Output.EskomTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                Reference = qm.Reference,
                Audience = qm.Audience,
                OfficeLocation = qm.OfficeLocation,
                Email = qm.Email,
                Address = qm.Address,
                Province = qm.Province,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.UtcNow,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue
            };
        }

        private Output.SanralTender MapSanralTender(Input.SanralTenderMessage? qm)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            return new Output.SanralTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                Category = qm.Category,
                // Region = qm.Region, // This field doesn't exist in the output model
                Email = qm.Email,
                // FullNoticeText = qm.FullNoticeText, // This field doesn't exist in the output model

                // Fields from DB model not in input model
                Institution = null,
                TenderType = null,
                Location = null,
                ContactPerson = null,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.UtcNow,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue
            };
        }

        private Output.TransnetTender MapTransnetTender(Input.TransnetTenderMessage? qm)
        {
            if (qm == null) throw new ArgumentNullException(nameof(qm));
            return new Output.TransnetTender
            {
                // Specific fields
                TenderNumber = qm.TenderNumber,
                Category = qm.Category,
                Email = qm.Email,
                // Institution = qm.Institution, // This field doesn't exist in the output model
                // TenderType = qm.TenderType, // This field doesn't exist in the output model
                // Location = qm.Location, // This field doesn't exist in the output model
                // ContactPerson = qm.ContactPerson, // This field doesn't exist in the output model

                // Fields from DB model not in input model
                Region = null,
                FullNoticeText = null,

                // Base fields
                PublishedDate = qm.PublishedDate ?? DateTime.UtcNow,
                ClosingDate = qm.ClosingDate ?? DateTime.MaxValue
            };
        }
    }
}
