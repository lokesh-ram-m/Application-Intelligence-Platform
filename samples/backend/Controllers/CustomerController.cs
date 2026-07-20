using Microsoft.AspNetCore.Mvc;
using ShopApi.Models;
using ShopApi.Services;

namespace ShopApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CustomerController : ControllerBase
{
    private readonly ICustomerService _service;

    public CustomerController(ICustomerService service) => _service = service;

    [HttpGet]
    public IEnumerable<Customer> GetAll() => _service.All();

    [HttpGet("{id}")]
    public Customer? Get(int id) => _service.Find(id);

    [HttpPost]
    public void Create(Customer customer) => _service.Add(customer);
}
