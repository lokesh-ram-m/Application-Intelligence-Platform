using ShopApi.Models;

namespace ShopApi.Services;

public interface ICustomerService
{
    IEnumerable<Customer> All();
    Customer? Find(int id);
    void Add(Customer customer);
}

public class CustomerService : ICustomerService
{
    private readonly List<Customer> _customers = new();

    public IEnumerable<Customer> All() => _customers;
    public Customer? Find(int id) => _customers.FirstOrDefault(c => c.Id == id);
    public void Add(Customer customer) => _customers.Add(customer);
}
