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
            modelBuilder.Entity<User>().ToContainer("Users");

            modelBuilder.Entity<User>()
                .HasKey(u => u.UniqueId);

            modelBuilder.Entity<User>().Property(u => u.Roles)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, default),
                    v => JsonSerializer.Deserialize<List<User.Role>>(v, default));

            modelBuilder.Entity<Home>().ToContainer("Homes");

            modelBuilder.Entity<Home>()
                .HasKey(h => h.Id);

            modelBuilder.Entity<Home>().Property(h => h.Residents)
                .HasConversion(
                v => JsonSerializer.Serialize(v, default),
                v => JsonSerializer.Deserialize<List<Resident>>(v, default));

            modelBuilder.Entity<Home>().Property(h => h.PhoneNumber)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, default),
                    v => JsonSerializer.Deserialize<PhoneNumber>(v, default));

            modelBuilder.Entity<Home>().Property(h => h.EmailAddress)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, default),
                    v => JsonSerializer.Deserialize<EmailAddress>(v, default));
        }

        public DbSet<User> Users { get; set; }

        public DbSet<Home> Homes { get; set; }
    }
}
