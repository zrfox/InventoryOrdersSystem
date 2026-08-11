using Microsoft.EntityFrameworkCore;
using OrdersService.Models;

namespace OrdersService.Data
    
{
    public class OrdersDbContext : DbContext
    {
        public OrdersDbContext(DbContextOptions<OrdersDbContext> options) 
            : base(options) { }

        public DbSet<Order> Orders => Set<Order>();
    }
}
