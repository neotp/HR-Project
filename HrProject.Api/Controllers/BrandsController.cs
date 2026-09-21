using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/brands")]
public sealed class BrandsController(Npgsql.NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BrandDto>>> GetBrands(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT brand.id, brand.brand_code, brand.brand_name,
                   brand.display_order, brand.is_active,
                   COUNT(employee.id) FILTER
                       (WHERE COALESCE(BTRIM(company.employee_status), '') <> 'ลาออก')::integer
                       AS employee_count
            FROM public.brands brand
            LEFT JOIN public.employee_brands relation ON relation.brand_id = brand.id
            LEFT JOIN public.employees employee
              ON employee.id = relation.employee_id
             AND employee.is_active = TRUE
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE (@include_inactive OR brand.is_active = TRUE)
            GROUP BY brand.id, brand.brand_code, brand.brand_name,
                     brand.display_order, brand.is_active
            ORDER BY brand.display_order, brand.brand_name, brand.id
            """;
        var result = new List<BrandDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new BrandDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetBoolean(4), reader.GetInt32(5)));
        return Ok(result);
    }

    [HttpGet("{brandId:long}/employees")]
    public async Task<ActionResult<IReadOnlyList<BrandEmployeeDto>>> GetEmployeesByBrand(
        long brandId,
        CancellationToken cancellationToken)
    {
        if (!await BrandExists(brandId, cancellationToken)) return NotFound("ไม่พบ Brand ที่ระบุ");
        const string sql = """
            SELECT employee.id, employee.employee_code,
                   COALESCE(NULLIF(BTRIM(basic.full_name_th), ''),
                            NULLIF(BTRIM(basic.full_name_en), ''), employee.employee_code),
                   COALESCE(company.department, ''),
                   COALESCE(company.position_name, ''),
                   COALESCE(basic.email_address, ''), relation.display_order
            FROM public.employee_brands relation
            JOIN public.employees employee ON employee.id = relation.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
            WHERE relation.brand_id = @brand_id
              AND employee.is_active = TRUE
              AND COALESCE(BTRIM(company.employee_status), '') <> 'ลาออก'
            ORDER BY relation.display_order, employee.employee_code
            """;
        return Ok(await ReadEmployees(sql, "brand_id", brandId, cancellationToken));
    }

    [HttpGet("employees/{employeeId:long}")]
    public async Task<ActionResult<IReadOnlyList<BrandDto>>> GetBrandsByEmployee(
        long employeeId,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        const string employeeSql = "SELECT EXISTS(SELECT 1 FROM public.employees WHERE id=@employee_id)";
        await using (var employeeCommand = dataSource.CreateCommand(employeeSql))
        {
            employeeCommand.Parameters.AddWithValue("employee_id", employeeId);
            if (!Convert.ToBoolean(await employeeCommand.ExecuteScalarAsync(cancellationToken)))
                return NotFound("ไม่พบพนักงานที่ระบุ");
        }

        const string sql = """
            SELECT brand.id, brand.brand_code, brand.brand_name,
                   relation.display_order, brand.is_active, 0
            FROM public.employee_brands relation
            JOIN public.brands brand ON brand.id = relation.brand_id
            WHERE relation.employee_id = @employee_id
              AND (@include_inactive OR brand.is_active = TRUE)
            ORDER BY relation.display_order, brand.display_order, brand.brand_name
            """;
        var result = new List<BrandDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new BrandDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetBoolean(4), reader.GetInt32(5)));
        return Ok(result);
    }

    private async Task<bool> BrandExists(long brandId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM public.brands WHERE id=@brand_id)";
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("brand_id", brandId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<List<BrandEmployeeDto>> ReadEmployees(
        string sql, string parameterName, long parameterValue, CancellationToken cancellationToken)
    {
        var result = new List<BrandEmployeeDto>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new BrandEmployeeDto(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
        return result;
    }
}
