using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TenderDatabaseWriterLambda.Models.Input;

namespace TenderDatabaseWriterLambda.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that transforms and writes
    /// a tender message to the database.
    /// </summary>
    public interface ITenderWriterService
    {
        /// <summary>
        /// Takes an input tender message from the queue, transforms it into
        /// a database entity, resolves all tag relationships, and saves it
        /// to the database.
        /// </summary>
        /// <param name="queueMessage">The deserialized tender message from the SQS queue.</param>
        Task WriteTenderAsync(TenderMessageBase queueMessage);
    }
}
