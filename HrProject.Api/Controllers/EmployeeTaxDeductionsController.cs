using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/employee-tax-deductions")]
[Authorize(Policy = "HrApiScope")]
public sealed class EmployeeTaxDeductionsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    private const string SelectColumns = """
        declaration.id, employee.id, employee.employee_code,
        COALESCE(NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), employee.employee_code),
        COALESCE(company.department, ''), @year,
        COALESCE(declaration.status, 'DRAFT'),
        COALESCE(declaration.marital_status, ''),
        declaration.is_marriage_registered,
        COALESCE(declaration.spouse_name, ''),
        declaration.spouse_has_income,
        COALESCE(declaration.spouse_national_id, ''),
        COALESCE(declaration.child_count, 0),
        COALESCE(declaration.child_born_from_2018_count, 0),
        COALESCE(declaration.disabled_dependent_count, 0),
        COALESCE(declaration.supports_father, FALSE), COALESCE(declaration.supports_mother, FALSE),
        COALESCE(declaration.supports_spouse_father, FALSE), COALESCE(declaration.supports_spouse_mother, FALSE),
        COALESCE(declaration.life_insurance_amount, 0),
        COALESCE(declaration.health_insurance_amount, 0),
        COALESCE(declaration.parent_health_insurance_amount, 0),
        COALESCE(declaration.provident_fund_amount, 0), COALESCE(declaration.retirement_fund_amount, 0),
        COALESCE(declaration.social_security_amount, 0), COALESCE(declaration.investment_deduction_amount, 0),
        COALESCE(declaration.donation_amount, 0), COALESCE(declaration.other_deduction_description, ''),
        COALESCE(declaration.other_deduction_amount, 0), COALESCE(declaration.note, ''),
        declaration.confirmed_at, declaration.updated_at
        """;

    [HttpGet("employee/{employeeId:int}")]
    public async Task<ActionResult<EmployeeTaxDeductionDto>> GetEmployeeDeclaration(
        int employeeId, [FromQuery] int year, CancellationToken cancellationToken)
    {
        if (!ValidYear(year)) return BadRequest("ปีภาษีไม่ถูกต้อง");
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (!await CanViewEmployee(actor, employeeId, cancellationToken)) return Forbid();

        const string fromSql = """
            FROM public.employees employee
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id=employee.id
            LEFT JOIN public.employee_family_info family ON family.employee_id=employee.id
            LEFT JOIN public.employee_tax_deduction_declarations declaration
              ON declaration.employee_id=employee.id AND declaration.tax_year=@year
            WHERE employee.id=@employee_id
            """;
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} {fromSql}");
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("year", year);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Ok(Read(reader)) : NotFound();
    }

    [HttpGet("employee/{employeeId:int}/years")]
    public async Task<ActionResult<IReadOnlyList<int>>> GetYears(int employeeId, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (!await CanViewEmployee(actor, employeeId, cancellationToken)) return Forbid();
        await using var command = dataSource.CreateCommand("""
            SELECT tax_year FROM public.employee_tax_deduction_declarations
            WHERE employee_id=@employee_id ORDER BY tax_year DESC
            """);
        command.Parameters.AddWithValue("employee_id", employeeId);
        var years = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) years.Add(reader.GetInt16(0));
        if (!years.Contains(DateTime.Today.Year)) years.Insert(0, DateTime.Today.Year);
        return Ok(years.Distinct().OrderDescending().ToList());
    }

    [HttpGet("employee/{employeeId:int}/{year:int}/history")]
    public async Task<ActionResult<IReadOnlyList<EmployeeTaxDeductionHistoryDto>>> GetHistory(
        int employeeId, int year, CancellationToken cancellationToken)
    {
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (!await CanViewEmployee(actor, employeeId, cancellationToken)) return Forbid();
        await using var command = dataSource.CreateCommand("""
            SELECT id, action, status, COALESCE(changed_by_name, changed_by), changed_at
            FROM public.employee_tax_deduction_history
            WHERE employee_id=@employee_id AND tax_year=@year
            ORDER BY changed_at DESC, id DESC
            """);
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("year", year);
        var rows = new List<EmployeeTaxDeductionHistoryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return Ok(rows);
    }

    [HttpPut("employee/{employeeId:int}/{year:int}")]
    public Task<ActionResult<EmployeeTaxDeductionDto>> SaveDraft(
        int employeeId, int year, [FromBody] EmployeeTaxDeductionDto request,
        CancellationToken cancellationToken) => Save(employeeId, year, request, false, cancellationToken);

    [HttpPost("employee/{employeeId:int}/{year:int}/confirm")]
    public Task<ActionResult<EmployeeTaxDeductionDto>> Confirm(
        int employeeId, int year, [FromBody] EmployeeTaxDeductionDto request,
        CancellationToken cancellationToken) => Save(employeeId, year, request, true, cancellationToken);

    [HttpGet("report")]
    public async Task<ActionResult<IReadOnlyList<EmployeeTaxDeductionDto>>> GetReport(
        [FromQuery] int year, CancellationToken cancellationToken)
    {
        if (!ValidYear(year)) return BadRequest("ปีภาษีไม่ถูกต้อง");
        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor))
            return Forbid();
        var canUseTaxReport = await pageAccessService.HasAccess(
            actor, "EMPLOYEE_TAX_DEDUCTION_REPORT", cancellationToken);
        var canExportFromEmployees =
            await pageAccessService.HasAccess(actor, "EMPLOYEES", cancellationToken) &&
            await actionPermissionService.HasPermission(
                actor, "EMPLOYEES", "EXPORT", cancellationToken);
        if (!canUseTaxReport && !canExportFromEmployees)
            return Forbid();

        var sql = $"""
            SELECT {SelectColumns}
            FROM public.employees employee
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            LEFT JOIN public.employee_company_info company ON company.employee_id=employee.id
            LEFT JOIN public.employee_family_info family ON family.employee_id=employee.id
            JOIN public.employee_tax_deduction_declarations declaration
              ON declaration.employee_id=employee.id AND declaration.tax_year=@year
            WHERE employee.is_active=TRUE
              AND COALESCE(BTRIM(company.employee_status), '') <> 'ลาออก'
            ORDER BY CASE WHEN employee.employee_code ~ '^[0-9]+$'
                     THEN employee.employee_code::NUMERIC END NULLS LAST, employee.employee_code
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("year", year);
        var rows = new List<EmployeeTaxDeductionDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return Ok(rows);
    }

    private async Task<ActionResult<EmployeeTaxDeductionDto>> Save(
        int employeeId, int year, EmployeeTaxDeductionDto request, bool confirm,
        CancellationToken cancellationToken)
    {
        if (!ValidYear(year)) return BadRequest("ปีภาษีไม่ถูกต้อง");
        if (request.ChildCount < 0 || request.ChildBornFrom2018Count < 0 || request.DisabledDependentCount < 0)
            return BadRequest("จำนวนบุคคลต้องไม่น้อยกว่า 0");
        if (Amounts(request).Any(value => value < 0)) return BadRequest("จำนวนเงินต้องไม่น้อยกว่า 0");

        var actor = await ResolveAuthenticatedEmployeeId(cancellationToken);
        var ownerCode = await GetEmployeeCode(employeeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(actor) ||
            !string.Equals(actor, ownerCode, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            INSERT INTO public.employee_tax_deduction_declarations
            (employee_id,tax_year,status,marital_status,is_marriage_registered,spouse_name,
             spouse_has_income,spouse_national_id,child_count,child_born_from_2018_count,
             disabled_dependent_count,supports_father,supports_mother,supports_spouse_father,
             supports_spouse_mother,life_insurance_amount,health_insurance_amount,
             parent_health_insurance_amount,provident_fund_amount,retirement_fund_amount,
             social_security_amount,investment_deduction_amount,donation_amount,
             other_deduction_description,other_deduction_amount,note,confirmed_at,confirmed_by,
             created_by,updated_by)
            VALUES
            (@employee_id,@year,@status,@marital,@registered,@spouse,@spouse_income,@spouse_national_id,
             @children,@children_2018,@disabled,@father,@mother,@spouse_father,@spouse_mother,
             @life,@health,@parent_health,@provident,@retirement,@social,@investment,@donation,
             @other_description,@other_amount,@note,
             CASE WHEN @status='CONFIRMED' THEN CURRENT_TIMESTAMP END,
             CASE WHEN @status='CONFIRMED' THEN @actor END,@actor,@actor)
            ON CONFLICT(employee_id,tax_year) DO UPDATE SET
             status=EXCLUDED.status,marital_status=EXCLUDED.marital_status,
             is_marriage_registered=EXCLUDED.is_marriage_registered,spouse_name=EXCLUDED.spouse_name,
             spouse_has_income=EXCLUDED.spouse_has_income,spouse_national_id=EXCLUDED.spouse_national_id,
             child_count=EXCLUDED.child_count,child_born_from_2018_count=EXCLUDED.child_born_from_2018_count,
             disabled_dependent_count=EXCLUDED.disabled_dependent_count,supports_father=EXCLUDED.supports_father,
             supports_mother=EXCLUDED.supports_mother,supports_spouse_father=EXCLUDED.supports_spouse_father,
             supports_spouse_mother=EXCLUDED.supports_spouse_mother,life_insurance_amount=EXCLUDED.life_insurance_amount,
             health_insurance_amount=EXCLUDED.health_insurance_amount,
             parent_health_insurance_amount=EXCLUDED.parent_health_insurance_amount,
             provident_fund_amount=EXCLUDED.provident_fund_amount,retirement_fund_amount=EXCLUDED.retirement_fund_amount,
             social_security_amount=EXCLUDED.social_security_amount,
             investment_deduction_amount=EXCLUDED.investment_deduction_amount,
             donation_amount=EXCLUDED.donation_amount,other_deduction_description=EXCLUDED.other_deduction_description,
             other_deduction_amount=EXCLUDED.other_deduction_amount,note=EXCLUDED.note,
             confirmed_at=EXCLUDED.confirmed_at,confirmed_by=EXCLUDED.confirmed_by,
             updated_at=CURRENT_TIMESTAMP,updated_by=EXCLUDED.updated_by
            RETURNING id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command, employeeId, year, request, confirm ? "CONFIRMED" : "DRAFT", actor);
        var declarationId = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("ไม่สามารถบันทึกข้อมูลค่าลดหย่อนได้"));

        await using var history = new NpgsqlCommand("""
            INSERT INTO public.employee_tax_deduction_history
                (declaration_id,employee_id,tax_year,action,status,data_snapshot,
                 changed_by,changed_by_name)
            SELECT declaration.id,declaration.employee_id,declaration.tax_year,@action,declaration.status,
                   to_jsonb(declaration),@actor,
                   COALESCE(NULLIF(basic.full_name_th,''),NULLIF(basic.full_name_en,''),@actor)
            FROM public.employee_tax_deduction_declarations declaration
            JOIN public.employees employee ON employee.id=declaration.employee_id
            LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            WHERE declaration.id=@id
            """, connection, transaction);
        history.Parameters.AddWithValue("action", confirm ? "CONFIRM" : "SAVE_DRAFT");
        history.Parameters.AddWithValue("actor", actor);
        history.Parameters.AddWithValue("id", declarationId);
        await history.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetEmployeeDeclaration(employeeId, year, cancellationToken);
    }

    private static void AddParameters(NpgsqlCommand command, int employeeId, int year,
        EmployeeTaxDeductionDto value, string status, string actor)
    {
        command.Parameters.AddWithValue("employee_id", employeeId);
        command.Parameters.AddWithValue("year", year);
        command.Parameters.AddWithValue("status", status);
        AddNullableText(command, "marital", value.MaritalStatus);
        command.Parameters.Add(new NpgsqlParameter<bool?>("registered", value.IsMarriageRegistered));
        AddNullableText(command, "spouse", value.SpouseName);
        command.Parameters.Add(new NpgsqlParameter<bool?>("spouse_income", value.SpouseHasIncome));
        AddNullableText(command, "spouse_national_id", value.SpouseNationalId);
        command.Parameters.AddWithValue("children", value.ChildCount);
        command.Parameters.AddWithValue("children_2018", value.ChildBornFrom2018Count);
        command.Parameters.AddWithValue("disabled", value.DisabledDependentCount);
        command.Parameters.AddWithValue("father", value.SupportsFather);
        command.Parameters.AddWithValue("mother", value.SupportsMother);
        command.Parameters.AddWithValue("spouse_father", value.SupportsSpouseFather);
        command.Parameters.AddWithValue("spouse_mother", value.SupportsSpouseMother);
        command.Parameters.AddWithValue("life", value.LifeInsuranceAmount);
        command.Parameters.AddWithValue("health", value.HealthInsuranceAmount);
        command.Parameters.AddWithValue("parent_health", value.ParentHealthInsuranceAmount);
        command.Parameters.AddWithValue("provident", value.ProvidentFundAmount);
        command.Parameters.AddWithValue("retirement", value.RetirementFundAmount);
        command.Parameters.AddWithValue("social", value.SocialSecurityAmount);
        command.Parameters.AddWithValue("investment", value.InvestmentDeductionAmount);
        command.Parameters.AddWithValue("donation", value.DonationAmount);
        AddNullableText(command, "other_description", value.OtherDeductionDescription);
        command.Parameters.AddWithValue("other_amount", value.OtherDeductionAmount);
        AddNullableText(command, "note", value.Note);
        command.Parameters.AddWithValue("actor", actor);
    }

    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter<string?>(name, NpgsqlDbType.Text)
            { TypedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });

    private static decimal[] Amounts(EmployeeTaxDeductionDto value) =>
        [value.LifeInsuranceAmount,value.HealthInsuranceAmount,value.ParentHealthInsuranceAmount,
         value.ProvidentFundAmount,value.RetirementFundAmount,value.SocialSecurityAmount,
         value.InvestmentDeductionAmount,value.DonationAmount,value.OtherDeductionAmount];

    private async Task<bool> CanViewEmployee(string? actor, int employeeId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(actor)) return false;
        var owner = await GetEmployeeCode(employeeId, token);
        if (string.Equals(actor, owner, StringComparison.OrdinalIgnoreCase)) return true;
        return await actionPermissionService.HasPermission(actor, "EMPLOYEES", "VIEW_PERSONAL", token)
            || await pageAccessService.HasAccess(actor, "EMPLOYEE_TAX_DEDUCTION_REPORT", token);
    }

    private async Task<string?> GetEmployeeCode(int employeeId, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT employee_code FROM public.employees WHERE id=@id LIMIT 1");
        command.Parameters.AddWithValue("id", employeeId);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken token)
    {
        var direct = User.FindFirst("employee_id")?.Value;
        if (!string.IsNullOrWhiteSpace(direct)) return direct.Trim();
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT employee_id FROM public.microsoft_accounts
            WHERE tenant_id=@tenant AND entra_object_id=@object AND is_active=TRUE LIMIT 1
            """);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("object", objectId);
        return (string?)await command.ExecuteScalarAsync(token);
    }

    private static bool ValidYear(int year) => year is >= 2000 and <= 2200;

    private static EmployeeTaxDeductionDto Read(NpgsqlDataReader reader) => new()
    {
        Id=reader.IsDBNull(0)?null:reader.GetInt64(0), EmployeeId=checked((int)reader.GetInt64(1)),
        EmployeeCode=reader.GetString(2), EmployeeName=reader.GetString(3), Department=reader.GetString(4),
        TaxYear=reader.GetInt32(5), Status=reader.GetString(6), MaritalStatus=reader.GetString(7),
        IsMarriageRegistered=reader.IsDBNull(8)?null:reader.GetBoolean(8), SpouseName=reader.GetString(9),
        SpouseHasIncome=reader.IsDBNull(10)?null:reader.GetBoolean(10), SpouseNationalId=reader.GetString(11),
        ChildCount=reader.GetInt32(12), ChildBornFrom2018Count=reader.GetInt32(13),
        DisabledDependentCount=reader.GetInt32(14), SupportsFather=reader.GetBoolean(15),
        SupportsMother=reader.GetBoolean(16), SupportsSpouseFather=reader.GetBoolean(17),
        SupportsSpouseMother=reader.GetBoolean(18), LifeInsuranceAmount=reader.GetDecimal(19),
        HealthInsuranceAmount=reader.GetDecimal(20), ParentHealthInsuranceAmount=reader.GetDecimal(21),
        ProvidentFundAmount=reader.GetDecimal(22), RetirementFundAmount=reader.GetDecimal(23),
        SocialSecurityAmount=reader.GetDecimal(24), InvestmentDeductionAmount=reader.GetDecimal(25),
        DonationAmount=reader.GetDecimal(26), OtherDeductionDescription=reader.GetString(27),
        OtherDeductionAmount=reader.GetDecimal(28), Note=reader.GetString(29),
        ConfirmedAt=reader.IsDBNull(30)?null:reader.GetFieldValue<DateTimeOffset>(30),
        UpdatedAt=reader.IsDBNull(31)?null:reader.GetFieldValue<DateTimeOffset>(31)
    };
}
