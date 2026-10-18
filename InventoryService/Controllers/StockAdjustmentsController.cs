using InventoryService.Data;
using InventoryService.Models;
using InventoryService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Controllers;

// Atomic, idempotent stock changes. OrdersService uses this for both reserving
// stock (negative change) and restoring it on cancellation (positive change).
[ApiController]
[Route("api/stock-adjustments")]
public class StockAdjustmentsController : ControllerBase
{
    private readonly InventoryDbContext _context;
    private readonly ProductLocks _locks;
    private readonly ILogger<StockAdjustmentsController> _logger;

    public StockAdjustmentsController(InventoryDbContext context, ProductLocks locks,
        ILogger<StockAdjustmentsController> logger)
    {
        _context = context;
        _locks = locks;
        _logger = logger;
    }

    // GET /api/stock-adjustments/{referenceKey}
    // 200 if the key was already applied, 404 otherwise.
    [HttpGet("{referenceKey}")]
    public async Task<ActionResult<StockAdjustment>> GetAdjustment(string referenceKey)
    {
        var adjustment = await _context.StockAdjustments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ReferenceKey == referenceKey);

        if (adjustment == null)
            return NotFound(new ErrorResponse($"No stock adjustment with reference key '{referenceKey}'."));

        return adjustment;
    }

    // POST /api/stock-adjustments
    // 200 applied (or alreadyApplied=true when the key was seen before)
    // 404 unknown product, 409 insufficient stock, 400 invalid request
    [HttpPost]
    public async Task<ActionResult<StockAdjustmentResponse>> ApplyAdjustment(StockAdjustmentRequest request)
    {
        if (request.QuantityChange == 0)
            return BadRequest(new ErrorResponse("QuantityChange must not be zero."));

        using (await _locks.AcquireAsync(request.ProductId, HttpContext.RequestAborted))
        {
            var existing = await FindExistingAsync(request.ReferenceKey);
            if (existing != null)
            {
                _logger.LogInformation("Stock adjustment {Key} was already applied; not applying it twice.", request.ReferenceKey);
                return Ok(await AlreadyAppliedResponseAsync(existing));
            }

            var product = await _context.Products.FindAsync(request.ProductId);
            if (product == null)
                return NotFound(new ErrorResponse($"Product {request.ProductId} not found."));

            var newQuantity = product.StockQuantity + request.QuantityChange;
            if (newQuantity < 0)
            {
                _logger.LogInformation("Rejected adjustment {Key}: product {ProductId} has {Stock}, requested {Requested}.",
                    request.ReferenceKey, product.Id, product.StockQuantity, -request.QuantityChange);
                return Conflict(new ErrorResponse(
                    $"Insufficient stock for product {product.Id} ('{product.Name}'): requested {-request.QuantityChange}, available {product.StockQuantity}.",
                    product.StockQuantity));
            }

            product.StockQuantity = newQuantity;
            var adjustment = new StockAdjustment
            {
                ReferenceKey = request.ReferenceKey,
                ProductId = product.Id,
                QuantityChange = request.QuantityChange,
                DateApplied = DateTime.UtcNow
            };
            _context.StockAdjustments.Add(adjustment);

            try
            {
                // product update + ledger row are saved in one transaction by EF
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Unique index on ReferenceKey rejected a concurrent duplicate (possible with
                // multiple Inventory instances). Undo tracked changes and answer idempotently.
                _context.Entry(adjustment).State = EntityState.Detached;
                await _context.Entry(product).ReloadAsync();
                var raced = await FindExistingAsync(request.ReferenceKey);
                if (raced == null) throw;
                return Ok(await AlreadyAppliedResponseAsync(raced));
            }

            _logger.LogInformation("Applied stock adjustment {Key}: product {ProductId} {Change:+#;-#;0} -> {Stock}.",
                request.ReferenceKey, product.Id, request.QuantityChange, product.StockQuantity);

            return Ok(new StockAdjustmentResponse
            {
                ProductId = product.Id,
                StockQuantity = product.StockQuantity,
                AlreadyApplied = false,
                ReferenceKey = request.ReferenceKey
            });
        }
    }

    private Task<StockAdjustment?> FindExistingAsync(string referenceKey) =>
        _context.StockAdjustments.AsNoTracking().FirstOrDefaultAsync(a => a.ReferenceKey == referenceKey);

    private async Task<StockAdjustmentResponse> AlreadyAppliedResponseAsync(StockAdjustment existing)
    {
        var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == existing.ProductId);
        return new StockAdjustmentResponse
        {
            ProductId = existing.ProductId,
            StockQuantity = product?.StockQuantity ?? 0,
            AlreadyApplied = true,
            ReferenceKey = existing.ReferenceKey
        };
    }
}
