using Microsoft.EntityFrameworkCore;
using InventoryService.Models;

namespace InventoryService.Data
{
    public class InventoryDbContext : DbContext
    {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
            : base(options) { }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // idempotency: a reference key can only ever be applied once
            modelBuilder.Entity<StockAdjustment>()
                .HasIndex(a => a.ReferenceKey)
                .IsUnique();
        }
    }
}
