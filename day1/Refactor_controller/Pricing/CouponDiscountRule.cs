namespace Refactor_controller.Pricing;

public class CouponDiscountRule : IDiscountRule
{
    private static readonly Dictionary<string, decimal> RatesByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAVE10"] = 0.10m,
        ["SAVE20"] = 0.20m,
    };

    public decimal Calculate(DiscountContext context)
    {
        if (context.CouponCode is null || !RatesByCode.TryGetValue(context.CouponCode, out var rate))
        {
            return 0m;
        }

        return context.Subtotal * rate;
    }
}
