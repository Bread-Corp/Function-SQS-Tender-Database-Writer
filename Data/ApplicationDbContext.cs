using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TenderDatabaseWriterLambda.Models.Output;

namespace TenderDatabaseWriterLambda.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<BaseTender> Tenders { get; set; }

        //Child Entities
        public DbSet<eTender> eTenders { get; set; }
        public DbSet<EskomTender> EskomTenders { get; set; }
        public DbSet<SanralTender> SanralTenders { get; set; }
        public DbSet<TransnetTender> TransnetTenders { get; set; }
        public DbSet<SarsTender> SarsTenders { get; set; }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            //base tender
            modelBuilder.Entity<BaseTender>(entity => { entity.ToTable("BaseTender"); });
            //children
            modelBuilder.Entity<eTender>(entity => { entity.ToTable("eTender"); });
            modelBuilder.Entity<EskomTender>(entity => { entity.ToTable("EskomTender"); });
            modelBuilder.Entity<SanralTender>(entity => { entity.ToTable("SanralTender"); });
            modelBuilder.Entity<TransnetTender>(entity => { entity.ToTable("TransnetTender"); });
            modelBuilder.Entity<SarsTender>(entity => { entity.ToTable("SarsTender"); });
        }
    }
}
