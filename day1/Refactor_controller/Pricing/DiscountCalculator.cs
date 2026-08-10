namespace Refactor_controller.Pricing;

public interface IDiscountCalculator
{
    decimal Calculate(DiscountContext context);
}

public class DiscountCalculator : IDiscountCalculator
{
    private readonly IEnumerable<IDiscountRule> _rules;

    public DiscountCalculator(IEnumerable<IDiscountRule> rules)
    {
        _rules = rules;
    }

    public decimal Calculate(DiscountContext context) =>
        _rules.Sum(rule => rule.Calculate(context));
}
