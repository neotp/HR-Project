using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EmployeesController : ControllerBase
{
    private static readonly List<Employee> Employees =
    [
        new() { Id = 1, EmployeeCode = "EMP-001", FirstName = "Anan", LastName = "Sukjai", Department = "Human Resources", Position = "HR Manager", Email = "anan@hrproject.local", StartDate = new DateOnly(2022, 1, 10) },
        new() { Id = 2, EmployeeCode = "EMP-002", FirstName = "Mali", LastName = "Dee", Department = "Finance", Position = "Accountant", Email = "mali@hrproject.local", StartDate = new DateOnly(2023, 5, 2) }
    ];

    [HttpGet]
    public ActionResult<IEnumerable<Employee>> GetAll() => Ok(Employees);

    [HttpGet("{id:int}")]
    public ActionResult<Employee> GetById(int id)
    {
        var employee = Employees.SingleOrDefault(x => x.Id == id);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost]
    public ActionResult<Employee> Create(Employee employee)
    {
        employee.Id = Employees.Count == 0 ? 1 : Employees.Max(x => x.Id) + 1;
        Employees.Add(employee);
        return CreatedAtAction(nameof(GetById), new { id = employee.Id }, employee);
    }

    internal static Employee? FindByEmail(string email) =>
        Employees.SingleOrDefault(employee =>
            string.Equals(employee.Email, email, StringComparison.OrdinalIgnoreCase));
}
