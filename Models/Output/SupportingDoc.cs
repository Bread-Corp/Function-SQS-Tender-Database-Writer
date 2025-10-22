using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TenderDatabaseWriterLambda.Models.Output
{
    [NotMapped]
    public class SupportingDoc
    {
        public string Name { get; set; }
        public string URL { get; set; }
    }
}
