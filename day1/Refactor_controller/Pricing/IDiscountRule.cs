namespace Refactor_controller.Pricing;

public record DiscountContext(decimal Subtotal, string? CouponCode);

public interface IDiscountRule
{
    decimal Calculate(DiscountContext context);
}
