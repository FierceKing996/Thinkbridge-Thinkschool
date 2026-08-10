using Refactor_controller.Dtos;

namespace Refactor_controller.Services;

public interface IOrderService
{
    Task<OrderResponse> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken);
    Task<OrderResponse> GetOrderAsync(int id, CancellationToken cancellationToken);
}
