using Microsoft.EntityFrameworkCore;
using InventoryService.Models;

namespace InventoryService.Data
{
    public class InventoryDbContext : DbContext
    {
        public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
            : base(options) { }

        public DbSet<Product> Products => Set<Product>();
        public DbSet<StockRestoration> StockRestorations => Set<StockRestoration>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StockRestoration>()
                .HasIndex(r => r.OperationId)
                .IsUnique();
        }
    }
}
