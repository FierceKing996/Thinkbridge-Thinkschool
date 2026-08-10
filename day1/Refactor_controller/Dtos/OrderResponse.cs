namespace Refactor_controller.Dtos;

public record OrderItemResponse(int ProductId, string ProductName, int Quantity, decimal UnitPrice);

public record OrderResponse(
    int OrderId,
    int CustomerId,
    string CustomerName,
    string Status,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<int> SkippedProductIds,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Total,
    DateTime CreatedAtUtc);
