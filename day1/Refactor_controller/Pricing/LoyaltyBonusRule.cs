namespace Refactor_controller.Pricing;

public class LoyaltyBonusRule : IDiscountRule
{
    private const decimal SpendThreshold = 500m;
    private const decimal BonusAmount = 15m;

    public decimal Calculate(DiscountContext context) =>
        context.Subtotal > SpendThreshold ? BonusAmount : 0m;
}
