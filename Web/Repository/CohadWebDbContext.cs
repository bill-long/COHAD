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

        private static Guid ParseLegacyGuid(string raw)
        {
            if (Guid.TryParse(raw, out var direct))
            {
                return direct;
            }

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var split = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var tail = split.Length > 0 ? split[split.Length - 1] : raw;
                if (Guid.TryParse(tail, out var legacy))
                {
                    return legacy;
                }
            }

            throw new FormatException($"Could not parse GUID value '{raw}'.");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().ToContainer("Users");

            modelBuilder.Entity<User>()
                .HasKey(u => u.UniqueId);

            // Cosmos legacy documents use id format "User|<UniqueId>".
            // Keep the model property as plain UniqueId while reading/writing the prefixed id.
            modelBuilder.Entity<User>().Property(u => u.UniqueId)
                .HasConversion(
                    v => v.StartsWith("User|", StringComparison.Ordinal) ? v : $"User|{v}",
                    v => !string.IsNullOrWhiteSpace(v) && v.StartsWith("User|", StringComparison.Ordinal)
                        ? v.Substring("User|".Length)
                        : v);

            modelBuilder.Entity<User>()
                .HasDiscriminator<string>("Discriminator")
                .HasValue<User>("User");

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

            // Keep Guid key on the model while exposing Cosmos string id as a shadow property.
            modelBuilder.Entity<Home>().HasShadowId();

            // Homes container stores a single CLR type. Disabling discriminator avoids
            // runtime materialization failures across mixed legacy document shapes.
            modelBuilder.Entity<Home>().HasNoDiscriminator();

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
            modelBuilder.Entity<NewAuditLogEntry>().Property(a => a.Id)
                .HasConversion(
                    v => $"AuditLog|{v:D}",
                    v => ParseLegacyGuid(v));

            modelBuilder.Entity<Payment>().ToContainer("Payments");

            modelBuilder.Entity<Payment>().HasKey(p => p.Id);
            modelBuilder.Entity<Payment>().HasNoDiscriminator();
            modelBuilder.Entity<Payment>().Property(p => p.Id)
                .HasConversion(
                    v => $"Payment|{v:D}",
                    v => ParseLegacyGuid(v));
        }

        public DbSet<User> Users { get; set; }

        public DbSet<Home> Homes { get; set; }

        public DbSet<Payment> Payments { get; set; }

        public DbSet<NewAuditLogEntry> AuditLog { get; set; }
    }
}
