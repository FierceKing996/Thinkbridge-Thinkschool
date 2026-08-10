using Refactor_controller.Dtos;
using Refactor_controller.Exceptions;
using Refactor_controller.Models;
using Refactor_controller.Repositories;

namespace Refactor_controller.Services;

public class OrderService : IOrderService
{
    private const decimal Save10DiscountRate = 0.10m;
    private const decimal Save20DiscountRate = 0.20m;
    private const decimal LoyaltyBonusThreshold = 500m;
    private const decimal LoyaltyBonusAmount = 15m;
    private const decimal TaxRate = 0.08m;

    private readonly ICustomerRepository _customerRepository;
    private readonly IProductRepository _productRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IEmailSender _emailSender;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        ICustomerRepository customerRepository,
        IProductRepository productRepository,
        IOrderRepository orderRepository,
        IEmailSender emailSender,
        ILogger<OrderService> logger)
    {
        _customerRepository = customerRepository;
        _productRepository = productRepository;
        _orderRepository = orderRepository;
        _emailSender = emailSender;
        _logger = logger;
    }

    public async Task<OrderResponse> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var customer = await _customerRepository.GetByIdAsync(request.CustomerId, cancellationToken)
            ?? throw new CustomerNotFoundException(request.CustomerId);

        var shippingCity = customer.Address?.City ?? "Unknown";
        _logger.LogInformation("Shipping order to city: {City}", shippingCity);

        var products = await _productRepository.GetByIdsAsync(
            request.Items.Select(i => i.ProductId), cancellationToken);

        var order = new Order
        {
            CustomerId = customer.Id,
            CreatedAtUtc = DateTime.UtcNow,
            Status = "Pending",
        };

        var itemResponses = new List<OrderItemResponse>();
        var skippedProductIds = new List<int>();
        decimal subtotal = 0m;

        foreach (var itemRequest in request.Items)
        {
            if (!products.TryGetValue(itemRequest.ProductId, out var product))
            {
                _logger.LogWarning("Product {ProductId} not found, skipping", itemRequest.ProductId);
                skippedProductIds.Add(itemRequest.ProductId);
                continue;
            }

            var categoryName = product.Category?.Name ?? "Uncategorized";
            _logger.LogInformation("Adding product from category: {Category}", categoryName);

            if (product.StockQuantity < itemRequest.Quantity)
            {
                _logger.LogWarning("Not enough stock for product {ProductId}, skipping", product.Id);
                skippedProductIds.Add(itemRequest.ProductId);
                continue;
            }

            product.StockQuantity -= itemRequest.Quantity;
            var lineTotal = product.Price * itemRequest.Quantity;
            subtotal += lineTotal;

            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                Quantity = itemRequest.Quantity,
                UnitPrice = product.Price
            });

            itemResponses.Add(new OrderItemResponse(product.Id, product.Name, itemRequest.Quantity, product.Price));
        }

        if (order.Items.Count == 0)
        {
            throw new NoOrderableItemsException();
        }

        var discount = CalculateDiscount(request.CouponCode, subtotal);
        var tax = (subtotal - discount) * TaxRate;
        var total = Math.Max(0, subtotal - discount + tax);

        order.Subtotal = subtotal;
        order.Discount = discount;
        order.Tax = tax;
        order.TotalAmount = total;

        await _orderRepository.AddAsync(order, cancellationToken);
        await _orderRepository.SaveChangesAsync(cancellationToken);

        await _emailSender.SendOrderConfirmationAsync(customer.Email, order.Id, total, cancellationToken);

        return new OrderResponse(
            order.Id,
            customer.Id,
            customer.Name,
            order.Status,
            itemResponses,
            skippedProductIds,
            subtotal,
            discount,
            tax,
            total,
            order.CreatedAtUtc);
    }

    public async Task<OrderResponse> GetOrderAsync(int id, CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new OrderNotFoundException(id);

        var itemResponses = order.Items
            .Select(i => new OrderItemResponse(i.ProductId, i.Product?.Name ?? "Unknown", i.Quantity, i.UnitPrice))
            .ToList();

        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Customer?.Name ?? "Unknown",
            order.Status,
            itemResponses,
            Array.Empty<int>(),
            order.Subtotal,
            order.Discount,
            order.Tax,
            order.TotalAmount,
            order.CreatedAtUtc);
    }

    private static decimal CalculateDiscount(string? couponCode, decimal subtotal)
    {
        var discount = couponCode switch
        {
            "SAVE10" => subtotal * Save10DiscountRate,
            "SAVE20" => subtotal * Save20DiscountRate,
            _ => 0m
        };

        if (subtotal > LoyaltyBonusThreshold)
        {
            discount += LoyaltyBonusAmount;
        }

        return discount;
    }
}
