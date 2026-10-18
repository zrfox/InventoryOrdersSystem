using Microsoft.EntityFrameworkCore;
using OrdersService.Models;

namespace OrdersService.Data
{
    public class OrdersDbContext : DbContext
    {
        public OrdersDbContext(DbContextOptions<OrdersDbContext> options)
            : base(options) { }

        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Nullable + unique: SQL Server gets a filtered unique index (NULLs are allowed).
            modelBuilder.Entity<Order>()
                .HasIndex(o => o.IdempotencyKey)
                .IsUnique();
        }
    }
}
