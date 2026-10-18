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
            // a client request id can only ever produce one order (double-submit protection)
            modelBuilder.Entity<Order>()
                .HasIndex(o => o.ClientRequestId)
                .IsUnique();
        }
    }
}
