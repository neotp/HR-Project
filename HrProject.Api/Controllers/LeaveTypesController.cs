using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-types")]
public sealed class LeaveTypesController(NpgsqlDataSource dataSource) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LeaveTypeDto>>> GetAll(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, code, name_th
            FROM public.leave_types
            WHERE is_active = TRUE
            ORDER BY id
            """;

        var result = new List<LeaveTypeDto>();
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LeaveTypeDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return Ok(result);
    }
}
