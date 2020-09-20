using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Web.Models;

namespace Web.Repository
{
    public class CohadWebDbContext : DbContext
    {
        public CohadWebDbContext(DbContextOptions<CohadWebDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>()
                .HasKey(u => u.NameIdentifier);

            modelBuilder.Entity<User>().Property(u => u.Roles)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, default),
                    v => JsonSerializer.Deserialize<List<User.Role>>(v, default));
        }

        public DbSet<User> Users { get; set; }
    }
}
