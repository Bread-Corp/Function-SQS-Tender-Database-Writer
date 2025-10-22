using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TenderDatabaseWriterLambda.Models.Output
{
    public class TransnetTender : BaseTender
    {
        [Required]
        public string? TenderNumber { get; set; }

        public string? Category { get; set; }

        public string? Region { get; set; }

        public string? Email { get; set; }

        public string? FullNoticeText { get; set; }
    }
}
