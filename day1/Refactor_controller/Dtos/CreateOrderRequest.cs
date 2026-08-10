using System.ComponentModel.DataAnnotations;

namespace Refactor_controller.Dtos;

public class CreateOrderRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "A valid customer id is required")]
    public int CustomerId { get; set; }

    public string? CouponCode { get; set; }

    [Required]
    [MinLength(1, ErrorMessage = "Order must contain at least one item")]
    public List<OrderItemRequest> Items { get; set; } = new();
}

public class OrderItemRequest
{
    [Range(1, int.MaxValue, ErrorMessage = "A valid product id is required")]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be positive")]
    public int Quantity { get; set; }
}
