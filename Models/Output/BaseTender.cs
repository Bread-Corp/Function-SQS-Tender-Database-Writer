using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TenderDatabaseWriterLambda.Models.Output
{
    public abstract class BaseTender : ITender
    {
        [Key]
        [Required]
        public Guid TenderID { get; set; } //Initialised on create.

        [Required]
        public string Title { get; set; }

        [Required]
        public string Status { get; set; } //Set on create.

        [Required]
        public DateTime PublishedDate { get; set; }

        [Required]
        public DateTime ClosingDate { get; set; }

        [Required]
        public DateTime DateAppended { get; set; }

        [Required]
        [JsonProperty("Source")]
        public string Source { get; set; } //Appended manually from lambda function.

        public List<Tag> Tags { get; set; } = new(); //Appended from comprehend.

        public string? Description { get; set; } //AI summary possibly.

        public string? AISummary { get; set; } //Another field for AI summary

        public List<SupportingDoc> SupportingDocs { get; set; } = new();
    }
}
