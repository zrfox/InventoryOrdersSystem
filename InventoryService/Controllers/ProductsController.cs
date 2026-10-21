using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InventoryService.Data;
using InventoryService.Models;

namespace InventoryService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    // How many times a stock change is re-tried when another request changed the same product first.
    private const int MaxSaveAttempts = 5;

    private readonly InventoryDbContext _context;

    public ProductsController(InventoryDbContext context)
    {
        _context = context;
    }

    // GET /api/products
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Product>>> GetProducts()
    {
        return await _context.Products.ToListAsync();
    }

    // GET /api/products/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetProduct(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();
        return product;
    }

    // POST /api/products
    [HttpPost]
    public async Task<ActionResult<Product>> CreateProduct(Product product)
    {
        _context.Products.Add(product);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, product);
    }

    // PUT /api/products/5
    // Loads the row first (instead of attaching the incoming object) so the
    // StockQuantity concurrency token compares against the real current value.
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProduct(int id, Product product)
    {
        if (id != product.Id) return BadRequest();

        var existing = await _context.Products.FindAsync(id);
        if (existing == null) return NotFound();

        existing.Name = product.Name;
        existing.Price = product.Price;
        existing.StockQuantity = product.StockQuantity;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("Stock changed while this update was being saved. Reload the product and try again.");
        }

        return NoContent();
    }

    // DELETE /api/products/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();
        _context.Products.Remove(product);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // PUT /api/products/5/stock
    // Manual stock edit with a staleness check: the caller says what stock it was shown
    // (ExpectedStockQuantity). If the stock is no longer that value — because an order,
    // a cancellation or another edit changed it — nothing is saved and 409 reports the
    // current value. The concurrency token covers the gap between our read and our write.
    [HttpPut("{id}/stock")]
    public async Task<ActionResult<Product>> SetStock(int id, SetStockRequest request)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null)
        {
            return NotFound(new StockError
            {
                Code = StockError.ProductNotFound,
                Message = $"Product {id} not found."
            });
        }

        if (product.StockQuantity != request.ExpectedStockQuantity)
        {
            return Conflict(StockChanged(id, request.ExpectedStockQuantity, product.StockQuantity));
        }

        product.StockQuantity = request.StockQuantity;

        try
        {
            await _context.SaveChangesAsync();
            return product;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed between our read above and the write: report what it is now.
            _context.ChangeTracker.Clear();
            var current = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            return Conflict(StockChanged(id, request.ExpectedStockQuantity, current?.StockQuantity ?? 0));
        }
    }

    private static StockError StockChanged(int id, int expected, int current)
    {
        return new StockError
        {
            Code = StockError.StockChanged,
            Message = $"Stock of product {id} changed from {expected} to {current} while you were editing. Nothing was saved.",
            StockQuantity = current
        };
    }

    // POST /api/products/5/stock/deduct
    // Atomically takes stock for an order. 409 = not enough stock (nothing changed).
    [HttpPost("{id}/stock/deduct")]
    public async Task<ActionResult<Product>> DeductStock(int id, DeductStockRequest request)
    {
        for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound(new StockError
                {
                    Code = StockError.ProductNotFound,
                    Message = $"Product {id} not found."
                });
            }

            if (product.StockQuantity < request.Quantity)
            {
                return Conflict(new StockError
                {
                    Code = StockError.InsufficientStock,
                    Message = $"Only {product.StockQuantity} of product {id} in stock.",
                    StockQuantity = product.StockQuantity
                });
            }

            product.StockQuantity -= request.Quantity;

            try
            {
                await _context.SaveChangesAsync();
                return product;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else changed the stock between our read and write: forget and re-read.
                _context.ChangeTracker.Clear();
            }
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new StockError
        {
            Code = StockError.Busy,
            Message = "Stock is being updated by too many requests at once. Try again."
        });
    }

    // POST /api/products/5/stock/restore
    // Idempotent: the stock is added and the OperationId recorded in ONE SaveChanges
    // (one transaction). Repeating the call with the same OperationId changes nothing.
    [HttpPost("{id}/stock/restore")]
    public async Task<ActionResult<RestoreStockResult>> RestoreStock(int id, RestoreStockRequest request)
    {
        if (request.OperationId == Guid.Empty)
        {
            return BadRequest(new StockError
            {
                Code = StockError.InvalidOperationId,
                Message = "operationId is required."
            });
        }

        for (var attempt = 0; attempt < MaxSaveAttempts; attempt++)
        {
            var alreadyApplied = await _context.StockRestorations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.OperationId == request.OperationId);

            if (alreadyApplied != null)
            {
                var current = await _context.Products
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == alreadyApplied.ProductId);

                return Ok(new RestoreStockResult
                {
                    Applied = false,
                    StockQuantity = current?.StockQuantity ?? 0
                });
            }

            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound(new StockError
                {
                    Code = StockError.ProductNotFound,
                    Message = $"Product {id} not found."
                });
            }

            product.StockQuantity += request.Quantity;
            _context.StockRestorations.Add(new StockRestoration
            {
                OperationId = request.OperationId,
                ProductId = id,
                Quantity = request.Quantity,
                AppliedAt = DateTime.UtcNow
            });

            try
            {
                await _context.SaveChangesAsync();
                return Ok(new RestoreStockResult { Applied = true, StockQuantity = product.StockQuantity });
            }
            catch (DbUpdateException)
            {
                // Either the stock changed under us (concurrency token) or the same OperationId
                // was inserted by a racing request (unique index). Both roll back this whole
                // save; loop to re-check from a clean slate.
                _context.ChangeTracker.Clear();
            }
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable, new StockError
        {
            Code = StockError.Busy,
            Message = "Stock is being updated by too many requests at once. Try again."
        });
    }
}