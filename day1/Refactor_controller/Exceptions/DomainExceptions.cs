namespace Refactor_controller.Exceptions;

public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
}

public class CustomerNotFoundException : DomainException
{
    public CustomerNotFoundException(int customerId)
        : base($"Customer {customerId} was not found") { }
}

public class NoOrderableItemsException : DomainException
{
    public NoOrderableItemsException()
        : base("None of the requested items could be ordered (unknown product or insufficient stock)") { }
}

public class OrderNotFoundException : DomainException
{
    public OrderNotFoundException(int orderId)
        : base($"Order {orderId} was not found") { }
}
