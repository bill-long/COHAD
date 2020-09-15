using System;
using Microsoft.EntityFrameworkCore;
using Web.Models;

namespace Web.Repository
{
    public class CohadWebDbContext : DbContext
    {
        public CohadWebDbContext(DbContextOptions<CohadWebDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().HasKey(u => u.NameIdentifier);
        }

        public DbSet<User> Users { get; set; }
    }
}
