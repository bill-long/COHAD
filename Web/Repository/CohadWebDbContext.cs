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
            modelBuilder.Entity<List<Guid>>().HasNoKey();

            modelBuilder.Entity<User>().ToContainer("Users");

            modelBuilder.Entity<User>()
                .HasKey(u => u.UniqueId);

            // EF Core insists on establishing a relationship for this list of GUIDs,
            // so we convert it to stop that behavior.
            modelBuilder.Entity<User>().Property(u => u.OwnedHomeIds)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions)default),
                    v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions)default));

            modelBuilder.Entity<User>().Property(u => u.Roles)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions) default),
                    v => JsonSerializer.Deserialize<List<User.Role>>(v, (JsonSerializerOptions) default));

            modelBuilder.Entity<Home>().ToContainer("Homes");

            modelBuilder.Entity<Home>()
                .HasKey(h => h.Id);

            modelBuilder.Entity<Home>().Property(h => h.Residents)
                .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions) default),
                v => JsonSerializer.Deserialize<List<Resident>>(v, (JsonSerializerOptions) default));

            modelBuilder.Entity<Home>().Property(h => h.PhoneNumber)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions) default),
                    v => JsonSerializer.Deserialize<PhoneNumber>(v, (JsonSerializerOptions) default));

            modelBuilder.Entity<Home>().Property(h => h.EmailAddress)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions) default),
                    v => JsonSerializer.Deserialize<EmailAddress>(v, (JsonSerializerOptions) default));

            modelBuilder.Entity<NewAuditLogEntry>().ToContainer("AuditLog");

            modelBuilder.Entity<NewAuditLogEntry>().HasKey(a => a.Id);
        }

        public DbSet<User> Users { get; set; }

        public DbSet<Home> Homes { get; set; }

        public DbSet<NewAuditLogEntry> AuditLog { get; set; }
    }
}
