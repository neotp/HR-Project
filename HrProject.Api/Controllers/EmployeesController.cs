using System.Text.Json;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EmployeesController(
    NpgsqlDataSource dataSource,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    private const string BaseSelect = """
        SELECT e.id, e.employee_code,
               b.title, b.first_name_th, b.last_name_th, b.full_name_th,
               b.first_name_en, b.last_name_en, b.full_name_en, b.nickname,
               COALESCE(b.email_address, '') AS email,
               b.personal_mobile, b.home_phone, b.profile_image_data,
               c.company_name, c.company_code, c.business_unit, c.division,
               c.department, c.section_name, c.position_name, c.job_code,
               c.supervisor_name, c.leave_approver_name, c.functional_supervisor_name,
               c.buddy_name, c.employment_type, c.work_schedule, c.work_location,
               c.employee_status, c.internal_extension, c.direct_phone,
               c.company_mobile, c.mac_address, c.branch_code, c.branch_name,
               c.responsibility_province, c.checklist_type, c.products_responsible,
               c.start_date, c.appointment_date, c.provident_fund_start_date,
               c.work_experience_type, c.has_company_parking, c.can_travel_upcountry,
               COALESCE(b.email_alias, '') AS lotus_notes_email,
               COALESCE(c.exclude_attendance_calculation, FALSE) AS exclude_attendance_calculation
        FROM public.employees e
        LEFT JOIN public.employee_basic_info b ON b.employee_id = e.id
        LEFT JOIN public.employee_company_info c ON c.employee_id = e.id
        """;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Employee>>> GetAll(CancellationToken cancellationToken)
    {
        var result = new List<Employee>();
        await using var command = dataSource.CreateCommand(
            BaseSelect + " WHERE e.is_active = TRUE ORDER BY e.employee_code");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadBaseEmployee(reader));
        return Ok(result);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Employee>> GetById(int id, CancellationToken cancellationToken)
    {
        var employee = await FindById(id, cancellationToken);
        return employee is null ? NotFound() : Ok(employee);
    }

    [HttpPost]
    public async Task<ActionResult<Employee>> Create(Employee employee, CancellationToken cancellationToken)
    {
        if (employee.EmployeeCode?.Trim().Length != 6)
            return BadRequest("รหัสพนักงานต้องมี 6 ตัว");

        if (string.IsNullOrWhiteSpace(employee.EmployeeCode) ||
            string.IsNullOrWhiteSpace(employee.FirstName) ||
            string.IsNullOrWhiteSpace(employee.LastName) ||
            string.IsNullOrWhiteSpace(employee.Email) ||
            string.IsNullOrWhiteSpace(employee.Department) ||
            string.IsNullOrWhiteSpace(employee.Position))
            return BadRequest("กรุณากรอกรหัสพนักงาน ชื่อ นามสกุล อีเมล แผนก และตำแหน่งให้ครบถ้วน");

        if (!IsValidProfileImage(employee.ProfileImageDataUrl))
            return BadRequest("รูปโปรไฟล์ไม่ถูกต้องหรือมีขนาดใหญ่เกิน 2 MB");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            employee.Id = checked((int)await InsertEmployee(connection, transaction, employee, cancellationToken));
            await UpsertEmployeeTabs(connection, transaction, employee, cancellationToken);
            await ReplaceHistories(connection, transaction, employee.Id, employee, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("รหัสพนักงานหรืออีเมลนี้มีอยู่ในระบบแล้ว");
        }

        return CreatedAtAction(nameof(GetById), new { id = employee.Id },
            await FindById(employee.Id, cancellationToken));
    }

    [HttpPut("{id:int}/self-service")]
    public async Task<ActionResult<Employee>> UpdateSelfService(
        int id, Employee submitted, CancellationToken cancellationToken)
    {
        return StatusCode(StatusCodes.Status403Forbidden, "ข้อมูลพนักงานต้องแก้ไขผ่านคำขอและรออนุมัติเท่านั้น");

#pragma warning disable CS0162
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string basicSql = """
            UPDATE public.employee_basic_info SET
                nickname = @nickname, personal_mobile = @mobile, home_phone = @home_phone
            WHERE employee_id = @id
            """;
        await using (var command = new NpgsqlCommand(basicSql, connection, transaction))
        {
            Add(command, "nickname", submitted.Nickname);
            Add(command, "mobile", submitted.PersonalMobile);
            Add(command, "home_phone", submitted.HomePhone);
            command.Parameters.AddWithValue("id", (long)id);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                return NotFound();
        }

        const string personalSql = """
            UPDATE public.employee_personal_info SET
                current_address = @address,
                emergency_contact_name = @emergency_name,
                emergency_contact_phone = @emergency_phone,
                emergency_contact_address = @emergency_address
            WHERE employee_id = @id
            """;
        await using (var command = new NpgsqlCommand(personalSql, connection, transaction))
        {
            Add(command, "address", submitted.CurrentAddress);
            Add(command, "emergency_name", submitted.EmergencyContactName);
            Add(command, "emergency_phone", submitted.EmergencyContactPhone);
            Add(command, "emergency_address", submitted.EmergencyContactAddress);
            command.Parameters.AddWithValue("id", (long)id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string familySql = """
            UPDATE public.employee_family_info SET
                family_phone = @phone, current_address_map_url = @map_url
            WHERE employee_id = @id
            """;
        await using (var command = new NpgsqlCommand(familySql, connection, transaction))
        {
            Add(command, "phone", submitted.FamilyPhone);
            Add(command, "map_url", submitted.CurrentAddressMapUrl);
            command.Parameters.AddWithValue("id", (long)id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceTrainingHistory(connection, transaction, id, submitted.TrainingHistory ?? [], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(await FindById(id, cancellationToken));
#pragma warning restore CS0162
    }

    [HttpPut("{id:int}/company")]
    public async Task<ActionResult<Employee>> UpdateCompany(
        int id,
        [FromQuery] string actorEmployeeId,
        Employee submitted,
        CancellationToken cancellationToken)
    {
        return StatusCode(StatusCodes.Status403Forbidden, "ข้อมูลพนักงานต้องแก้ไขผ่านคำขอและรออนุมัติเท่านั้น");

#pragma warning disable CS0162
        var authenticatedEmployeeId = await ResolveAuthenticatedEmployeeId(cancellationToken);
        if (string.IsNullOrWhiteSpace(authenticatedEmployeeId) ||
            string.IsNullOrWhiteSpace(actorEmployeeId) ||
            !string.Equals(authenticatedEmployeeId, actorEmployeeId.Trim(), StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status403Forbidden, "บัญชีผู้ใช้งานไม่ตรงกับผู้แก้ไขข้อมูล");

        if (!await actionPermissionService.HasPermission(
                authenticatedEmployeeId, "EMPLOYEES", "EDIT", cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, "ไม่มีสิทธิ์แก้ไขข้อมูลพนักงานโดยตรง");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string basicSql = """
                UPDATE public.employee_basic_info
                SET email_alias = @lotus_email,
                    email_address = @email
                WHERE employee_id = @id
                """;
            await using (var command = new NpgsqlCommand(basicSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", (long)id);
                Add(command, "lotus_email", submitted.LotusNotesEmail);
                Add(command, "email", submitted.Email);
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                    return NotFound();
            }

            const string companySql = """
                UPDATE public.employee_company_info SET
                    company_name = @company, business_unit = @bu, division = @division,
                    department = @department, section_name = @section, position_name = @position,
                    job_code = @job, supervisor_name = @supervisor,
                    leave_approver_name = @approver,
                    functional_supervisor_name = @functional, buddy_name = @buddy,
                    employment_type = @employment, work_schedule = @schedule,
                    work_location = @location, employee_status = @status,
                    internal_extension = @extension, direct_phone = @direct,
                    company_mobile = @company_mobile, mac_address = @mac,
                    branch_code = @branch_code, branch_name = @branch_name,
                    responsibility_province = @province, checklist_type = @checklist,
                    products_responsible = @products, start_date = @start,
                    appointment_date = @appointment,
                    provident_fund_start_date = @fund,
                    work_experience_type = @experience,
                    has_company_parking = @parking,
                    can_travel_upcountry = @travel,
                    exclude_attendance_calculation = @exclude_attendance
                WHERE employee_id = @id
                """;
            await using (var command = new NpgsqlCommand(companySql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", (long)id);
                Add(command, "company", submitted.Company); Add(command, "bu", submitted.BusinessUnit);
                Add(command, "division", submitted.Division); Add(command, "department", submitted.Department);
                Add(command, "section", submitted.Section); Add(command, "position", submitted.Position);
                Add(command, "job", submitted.JobCode); Add(command, "supervisor", submitted.SupervisorName);
                Add(command, "approver", submitted.LeaveApproverName); Add(command, "functional", submitted.FunctionalSupervisorName);
                Add(command, "buddy", submitted.BuddyName); Add(command, "employment", submitted.EmploymentType);
                Add(command, "schedule", submitted.WorkSchedule); Add(command, "location", submitted.WorkLocation);
                Add(command, "status", submitted.EmployeeStatus); Add(command, "extension", submitted.InternalExtension);
                Add(command, "direct", submitted.DirectPhone); Add(command, "company_mobile", submitted.CompanyMobile);
                Add(command, "mac", submitted.MacAddress); Add(command, "branch_code", submitted.BranchCode);
                Add(command, "branch_name", submitted.BranchName); Add(command, "province", submitted.ResponsibilityProvince);
                Add(command, "checklist", submitted.ChecklistType); Add(command, "products", submitted.ProductsResponsible);
                AddDate(command, "start", submitted.StartDate == default ? null : submitted.StartDate);
                AddDate(command, "appointment", submitted.AppointmentDate); AddDate(command, "fund", submitted.ProvidentFundStartDate);
                Add(command, "experience", submitted.WorkExperienceType); AddBoolean(command, "parking", submitted.HasCompanyParking);
                AddBoolean(command, "travel", submitted.CanTravelUpcountry);
                command.Parameters.AddWithValue("exclude_attendance", submitted.ExcludeAttendanceCalculation);
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                    return NotFound();
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("Email นี้มีอยู่ในระบบแล้ว");
        }

        return Ok(await FindById(id, cancellationToken));
#pragma warning restore CS0162
    }

    internal static async Task<bool> ApplyApprovedChanges(
        NpgsqlDataSource source,
        string employeeCode,
        IEnumerable<EmployeeFieldChangeDto> changes,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long? employeeId;
        await using (var command = new NpgsqlCommand(
            "SELECT id FROM public.employees WHERE employee_code = @code FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("code", employeeCode);
            employeeId = (long?)await command.ExecuteScalarAsync(cancellationToken);
        }
        if (!employeeId.HasValue)
            return false;

        foreach (var change in changes)
            await ApplyApprovedChange(connection, transaction, employeeId.Value, change, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static async Task<Employee?> FindByFullName(
        NpgsqlDataSource source, string fullName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return null;

        var normalizedName = string.Join(' ', fullName
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

        const string sql = """
            SELECT e.id
            FROM public.employees e
            JOIN public.employee_basic_info b ON b.employee_id = e.id
            WHERE e.is_active = TRUE
              AND @name IN
                  (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_th, ''))), '\s+', ' ', 'g'),
                   REGEXP_REPLACE(UPPER(BTRIM(COALESCE(b.full_name_en, ''))), '\s+', ' ', 'g'),
                   REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_th, b.last_name_th))), '\s+', ' ', 'g'),
                   REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ', b.first_name_en, b.last_name_en))), '\s+', ' ', 'g'))
            ORDER BY e.id
            LIMIT 2
            """;
        long matchedId;
        await using (var command = source.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("name", normalizedName);

            var matchingIds = new List<long>(2);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                matchingIds.Add(reader.GetInt64(0));

            if (matchingIds.Count != 1)
                return null;
            matchedId = matchingIds[0];
        }

        return await FindById(source, checked((int)matchedId), cancellationToken);
    }

    private async Task<Employee?> FindById(int id, CancellationToken cancellationToken) =>
        await FindById(dataSource, id, cancellationToken);

    private static async Task<Employee?> FindById(
        NpgsqlDataSource source, int id, CancellationToken cancellationToken)
    {
        Employee? employee;
        await using (var command = source.CreateCommand(BaseSelect + " WHERE e.id = @id"))
        {
            command.Parameters.AddWithValue("id", (long)id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            employee = await reader.ReadAsync(cancellationToken) ? ReadBaseEmployee(reader) : null;
        }
        if (employee is null)
            return null;
        await LoadPersonal(source, employee, cancellationToken);
        await LoadFamily(source, employee, cancellationToken);
        await LoadHistories(source, employee, cancellationToken);
        return employee;
    }

    private static Employee ReadBaseEmployee(NpgsqlDataReader reader) => new()
    {
        Id = checked((int)reader.GetInt64(0)), EmployeeCode = S(reader, 1),
        Title = S(reader, 2), FirstName = S(reader, 3), LastName = S(reader, 4),
        ThaiFullName = S(reader, 5), EnglishFullName = S(reader, 8), Nickname = S(reader, 9),
        Email = S(reader, 10), PersonalMobile = S(reader, 11), HomePhone = S(reader, 12),
        ProfileImageDataUrl = S(reader, 13), Company = S(reader, 14, S(reader, 15)),
        BusinessUnit = S(reader, 16), Division = S(reader, 17), Department = S(reader, 18),
        Section = S(reader, 19), Position = S(reader, 20), JobCode = S(reader, 21),
        SupervisorName = S(reader, 22), LeaveApproverName = S(reader, 23),
        FunctionalSupervisorName = S(reader, 24), BuddyName = S(reader, 25),
        EmploymentType = S(reader, 26), WorkSchedule = S(reader, 27), WorkLocation = S(reader, 28),
        EmployeeStatus = S(reader, 29), InternalExtension = S(reader, 30), DirectPhone = S(reader, 31),
        CompanyMobile = S(reader, 32), MacAddress = S(reader, 33), BranchCode = S(reader, 34),
        BranchName = S(reader, 35), ResponsibilityProvince = S(reader, 36),
        ChecklistType = S(reader, 37), ProductsResponsible = S(reader, 38),
        StartDate = D(reader, 39) ?? default, AppointmentDate = D(reader, 40),
        ProvidentFundStartDate = D(reader, 41), WorkExperienceType = S(reader, 42),
        HasCompanyParking = B(reader, 43), CanTravelUpcountry = B(reader, 44),
        LotusNotesEmail = S(reader, 45), ExcludeAttendanceCalculation = reader.GetBoolean(46)
    };

    private static async Task LoadPersonal(NpgsqlDataSource source, Employee employee, CancellationToken token)
    {
        const string sql = """
            SELECT religion, blood_type, residence_province, current_address,
                   id_card_address, house_registration_address,
                   emergency_contact_name, emergency_contact_phone, emergency_contact_address
            FROM public.employee_personal_info WHERE employee_id = @id
            """;
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("id", (long)employee.Id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return;
        employee.Religion = S(reader, 0); employee.BloodType = S(reader, 1);
        employee.ResidenceProvince = S(reader, 2); employee.CurrentAddress = S(reader, 3);
        employee.IdCardAddress = S(reader, 4); employee.HouseRegistrationAddress = S(reader, 5);
        employee.EmergencyContactName = S(reader, 6); employee.EmergencyContactPhone = S(reader, 7);
        employee.EmergencyContactAddress = S(reader, 8);
    }

    private static async Task LoadFamily(NpgsqlDataSource source, Employee employee, CancellationToken token)
    {
        const string sql = """
            SELECT family_member_name, family_relationship, family_phone, family_occupation,
                   marital_status, is_marriage_registered, spouse_title, spouse_name, marriage_date,
                   spouse_has_income, spouse_national_id, spouse_passport_id, spouse_passport_name,
                   spouse_passport_file_name, uneducated_child_count, studying_child_count,
                   life_insurance_amount, parent_support_deduction_amount,
                   spouse_parent_deduction_amount, current_address_map_url
            FROM public.employee_family_info WHERE employee_id = @id
            """;
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue("id", (long)employee.Id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return;
        employee.FamilyMemberName=S(reader,0); employee.FamilyRelationship=S(reader,1);
        employee.FamilyPhone=S(reader,2); employee.FamilyOccupation=S(reader,3);
        employee.MaritalStatus=S(reader,4); employee.IsMarriageRegistered=B(reader,5);
        employee.SpouseTitle=S(reader,6); employee.SpouseName=S(reader,7); employee.MarriageDate=D(reader,8);
        employee.SpouseHasIncome=B(reader,9); employee.SpouseNationalId=S(reader,10);
        employee.SpousePassportId=S(reader,11); employee.SpousePassportName=S(reader,12);
        employee.SpousePassportFileName=S(reader,13); employee.UneducatedChildCount=I(reader,14);
        employee.StudyingChildCount=I(reader,15); employee.LifeInsuranceAmount=M(reader,16);
        employee.ParentSupportDeductionAmount=M(reader,17); employee.SpouseParentSupportDeductionAmount=M(reader,18);
        employee.CurrentAddressMapUrl=S(reader,19);
    }

    private static async Task LoadHistories(NpgsqlDataSource source, Employee employee, CancellationToken token)
    {
        await using (var command = source.CreateCommand("SELECT period_text, position_name, company_name, details FROM public.employee_work_history WHERE employee_id=@id ORDER BY display_order"))
        {
            command.Parameters.AddWithValue("id", (long)employee.Id);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) employee.WorkHistory.Add(new EmployeeWorkHistoryItem { Period=S(reader,0), Position=S(reader,1), Company=S(reader,2) });
        }
        await using (var command = source.CreateCommand("SELECT education_level, institution_name, major_name, graduation_year FROM public.employee_education_history WHERE employee_id=@id ORDER BY display_order"))
        {
            command.Parameters.AddWithValue("id", (long)employee.Id);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) employee.EducationHistory.Add(new EmployeeEducationItem { Level=S(reader,0), Institution=S(reader,1), Major=S(reader,2), GraduationYear=S(reader,3) });
        }
        await using (var command = source.CreateCommand("SELECT course_name, training_period, location_name, expense, certificate, exam_fee FROM public.employee_training_history WHERE employee_id=@id ORDER BY display_order"))
        {
            command.Parameters.AddWithValue("id", (long)employee.Id);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) employee.TrainingHistory.Add(new EmployeeTrainingItem { CourseName=S(reader,0), TrainingPeriod=S(reader,1), Location=S(reader,2), Expense=M(reader,3), Certificate=S(reader,4), ExamFee=M(reader,5) });
        }
    }

    private static async Task<long> InsertEmployee(NpgsqlConnection c, NpgsqlTransaction t, Employee e, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("INSERT INTO public.employees(employee_code,is_active,source_system) VALUES(@code,TRUE,'HR_APP') RETURNING id", c, t);
        command.Parameters.AddWithValue("code", e.EmployeeCode.Trim());
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task UpsertEmployeeTabs(NpgsqlConnection c, NpgsqlTransaction t, Employee e, CancellationToken token)
    {
        const string basic = """
            INSERT INTO public.employee_basic_info(employee_id,title,first_name_th,last_name_th,full_name_th,full_name_en,nickname,email_alias,email_address,personal_mobile,home_phone,profile_image_data)
            VALUES(@id,@title,@first,@last,@thai,@english,@nickname,@lotus_notes_email,@email,@mobile,@home,@image)
            """;
        await using (var command = new NpgsqlCommand(basic,c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"title",e.Title); Add(command,"first",e.FirstName); Add(command,"last",e.LastName); Add(command,"thai",e.ThaiFullName); Add(command,"english",e.EnglishFullName); Add(command,"nickname",e.Nickname); Add(command,"lotus_notes_email",e.LotusNotesEmail); Add(command,"email",e.Email); Add(command,"mobile",e.PersonalMobile); Add(command,"home",e.HomePhone); Add(command,"image",e.ProfileImageDataUrl); await command.ExecuteNonQueryAsync(token); }
        const string company = """
            INSERT INTO public.employee_company_info(employee_id,company_name,business_unit,division,department,section_name,position_name,job_code,supervisor_name,leave_approver_name,functional_supervisor_name,buddy_name,employment_type,work_schedule,work_location,employee_status,internal_extension,direct_phone,company_mobile,mac_address,branch_code,branch_name,responsibility_province,checklist_type,products_responsible,start_date,appointment_date,provident_fund_start_date,work_experience_type,has_company_parking,can_travel_upcountry,exclude_attendance_calculation)
            VALUES(@id,@company,@bu,@division,@department,@section,@position,@job,@supervisor,@approver,@functional,@buddy,@employment,@schedule,@location,@status,@extension,@direct,@company_mobile,@mac,@branch_code,@branch_name,@province,@checklist,@products,@start,@appointment,@fund,@experience,@parking,@travel,@exclude_attendance)
            """;
        await using (var command = new NpgsqlCommand(company,c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"company",e.Company); Add(command,"bu",e.BusinessUnit); Add(command,"division",e.Division); Add(command,"department",e.Department); Add(command,"section",e.Section); Add(command,"position",e.Position); Add(command,"job",e.JobCode); Add(command,"supervisor",e.SupervisorName); Add(command,"approver",e.LeaveApproverName); Add(command,"functional",e.FunctionalSupervisorName); Add(command,"buddy",e.BuddyName); Add(command,"employment",e.EmploymentType); Add(command,"schedule",e.WorkSchedule); Add(command,"location",e.WorkLocation); Add(command,"status",e.EmployeeStatus); Add(command,"extension",e.InternalExtension); Add(command,"direct",e.DirectPhone); Add(command,"company_mobile",e.CompanyMobile); Add(command,"mac",e.MacAddress); Add(command,"branch_code",e.BranchCode); Add(command,"branch_name",e.BranchName); Add(command,"province",e.ResponsibilityProvince); Add(command,"checklist",e.ChecklistType); Add(command,"products",e.ProductsResponsible); AddDate(command,"start",e.StartDate==default?null:e.StartDate); AddDate(command,"appointment",e.AppointmentDate); AddDate(command,"fund",e.ProvidentFundStartDate); Add(command,"experience",e.WorkExperienceType); AddBoolean(command,"parking",e.HasCompanyParking); AddBoolean(command,"travel",e.CanTravelUpcountry); command.Parameters.AddWithValue("exclude_attendance",e.ExcludeAttendanceCalculation); await command.ExecuteNonQueryAsync(token); }
        await using (var command = new NpgsqlCommand("INSERT INTO public.employee_personal_info(employee_id,religion,blood_type,residence_province,current_address,id_card_address,house_registration_address,emergency_contact_name,emergency_contact_phone,emergency_contact_address) VALUES(@id,@religion,@blood,@province,@current,@id_address,@house,@emergency,@phone,@emergency_address)",c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"religion",e.Religion); Add(command,"blood",e.BloodType); Add(command,"province",e.ResidenceProvince); Add(command,"current",e.CurrentAddress); Add(command,"id_address",e.IdCardAddress); Add(command,"house",e.HouseRegistrationAddress); Add(command,"emergency",e.EmergencyContactName); Add(command,"phone",e.EmergencyContactPhone); Add(command,"emergency_address",e.EmergencyContactAddress); await command.ExecuteNonQueryAsync(token); }
        await using (var command = new NpgsqlCommand("INSERT INTO public.employee_family_info(employee_id,marital_status,family_member_name,family_relationship,family_phone,family_occupation,current_address_map_url) VALUES(@id,@marital,@name,@relationship,@phone,@occupation,@map)",c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"marital",e.MaritalStatus); Add(command,"name",e.FamilyMemberName); Add(command,"relationship",e.FamilyRelationship); Add(command,"phone",e.FamilyPhone); Add(command,"occupation",e.FamilyOccupation); Add(command,"map",e.CurrentAddressMapUrl); await command.ExecuteNonQueryAsync(token); }
    }

    private static async Task ReplaceHistories(NpgsqlConnection c, NpgsqlTransaction t, int id, Employee e, CancellationToken token)
    { await ReplaceWorkHistory(c,t,id,e.WorkHistory??[],token); await ReplaceEducationHistory(c,t,id,e.EducationHistory??[],token); await ReplaceTrainingHistory(c,t,id,e.TrainingHistory??[],token); }
    private static async Task ReplaceWorkHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeWorkHistoryItem> rows,CancellationToken token) { await Delete(c,t,"employee_work_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_work_history(employee_id,display_order,period_text,position_name,company_name) VALUES(@id,@order,@period,@position,@company)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"period",rows[i].Period); Add(q,"position",rows[i].Position); Add(q,"company",rows[i].Company); await q.ExecuteNonQueryAsync(token); } }
    private static async Task ReplaceEducationHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeEducationItem> rows,CancellationToken token) { await Delete(c,t,"employee_education_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_education_history(employee_id,display_order,education_level,institution_name,major_name,graduation_year) VALUES(@id,@order,@level,@institution,@major,@year)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"level",rows[i].Level); Add(q,"institution",rows[i].Institution); Add(q,"major",rows[i].Major); Add(q,"year",rows[i].GraduationYear); await q.ExecuteNonQueryAsync(token); } }
    private static async Task ReplaceTrainingHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeTrainingItem> rows,CancellationToken token) { await Delete(c,t,"employee_training_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_training_history(employee_id,display_order,course_name,training_period,location_name,expense,certificate,exam_fee) VALUES(@id,@order,@course,@period,@location,@expense,@certificate,@exam)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"course",rows[i].CourseName); Add(q,"period",rows[i].TrainingPeriod); Add(q,"location",rows[i].Location); q.Parameters.AddWithValue("expense",rows[i].Expense); Add(q,"certificate",rows[i].Certificate); q.Parameters.AddWithValue("exam",rows[i].ExamFee); await q.ExecuteNonQueryAsync(token); } }
    private static async Task Delete(NpgsqlConnection c,NpgsqlTransaction t,string table,long id,CancellationToken token){ await using var q=new NpgsqlCommand($"DELETE FROM public.{table} WHERE employee_id=@id",c,t); q.Parameters.AddWithValue("id",id); await q.ExecuteNonQueryAsync(token); }

    private static async Task ApplyApprovedChange(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long employeeId,
        EmployeeFieldChangeDto change,
        CancellationToken cancellationToken)
    {
        var value = change.NewValue?.Trim() ?? string.Empty;
        var textSql = change.FieldKey switch
        {
            "profileImage" => "UPDATE public.employee_basic_info SET profile_image_data=@value WHERE employee_id=@id",
            "title" => "UPDATE public.employee_basic_info SET title=@value WHERE employee_id=@id",
            "firstName" => "UPDATE public.employee_basic_info SET first_name_th=@value WHERE employee_id=@id",
            "lastName" => "UPDATE public.employee_basic_info SET last_name_th=@value WHERE employee_id=@id",
            "thaiFullName" => "UPDATE public.employee_basic_info SET full_name_th=@value WHERE employee_id=@id",
            "englishFullName" => "UPDATE public.employee_basic_info SET full_name_en=@value WHERE employee_id=@id",
            "nickname" => "UPDATE public.employee_basic_info SET nickname=@value WHERE employee_id=@id",
            "lotusNotesEmail" => "UPDATE public.employee_basic_info SET email_alias=@value WHERE employee_id=@id",
            "email" => "UPDATE public.employee_basic_info SET email_address=@value WHERE employee_id=@id",
            "personalMobile" => "UPDATE public.employee_basic_info SET personal_mobile=@value WHERE employee_id=@id",
            "homePhone" => "UPDATE public.employee_basic_info SET home_phone=@value WHERE employee_id=@id",
            "personal.nationalId" => "UPDATE public.employee_personal_info SET national_id=@value WHERE employee_id=@id",
            "personal.gender" => "UPDATE public.employee_personal_info SET gender=@value WHERE employee_id=@id",
            "personal.religion" => "UPDATE public.employee_personal_info SET religion=@value WHERE employee_id=@id",
            "personal.bloodType" => "UPDATE public.employee_personal_info SET blood_type=@value WHERE employee_id=@id",
            "personal.residenceProvince" => "UPDATE public.employee_personal_info SET residence_province=@value WHERE employee_id=@id",
            "personal.currentAddress" => "UPDATE public.employee_personal_info SET current_address=@value WHERE employee_id=@id",
            "personal.idCardAddress" => "UPDATE public.employee_personal_info SET id_card_address=@value WHERE employee_id=@id",
            "personal.houseRegistrationAddress" => "UPDATE public.employee_personal_info SET house_registration_address=@value WHERE employee_id=@id",
            "personal.emergencyContactName" => "UPDATE public.employee_personal_info SET emergency_contact_name=@value WHERE employee_id=@id",
            "personal.emergencyContactPhone" => "UPDATE public.employee_personal_info SET emergency_contact_phone=@value WHERE employee_id=@id",
            "personal.emergencyContactAddress" => "UPDATE public.employee_personal_info SET emergency_contact_address=@value WHERE employee_id=@id",
            "internal.company" => "UPDATE public.employee_company_info SET company_name=@value WHERE employee_id=@id",
            "internal.businessUnit" => "UPDATE public.employee_company_info SET business_unit=@value WHERE employee_id=@id",
            "internal.division" => "UPDATE public.employee_company_info SET division=@value WHERE employee_id=@id",
            "internal.department" => "UPDATE public.employee_company_info SET department=@value WHERE employee_id=@id",
            "internal.section" => "UPDATE public.employee_company_info SET section_name=@value WHERE employee_id=@id",
            "internal.position" => "UPDATE public.employee_company_info SET position_name=@value WHERE employee_id=@id",
            "internal.jobCode" => "UPDATE public.employee_company_info SET job_code=@value WHERE employee_id=@id",
            "internal.supervisor" => "UPDATE public.employee_company_info SET supervisor_name=@value WHERE employee_id=@id",
            "internal.leaveApprover" => "UPDATE public.employee_company_info SET leave_approver_name=@value WHERE employee_id=@id",
            "internal.functionalSupervisor" => "UPDATE public.employee_company_info SET functional_supervisor_name=@value WHERE employee_id=@id",
            "internal.buddy" => "UPDATE public.employee_company_info SET buddy_name=@value WHERE employee_id=@id",
            "internal.employmentType" => "UPDATE public.employee_company_info SET employment_type=@value WHERE employee_id=@id",
            "internal.workSchedule" => "UPDATE public.employee_company_info SET work_schedule=@value WHERE employee_id=@id",
            "internal.workLocation" => "UPDATE public.employee_company_info SET work_location=@value WHERE employee_id=@id",
            "internal.extension" => "UPDATE public.employee_company_info SET internal_extension=@value WHERE employee_id=@id",
            "internal.directPhone" => "UPDATE public.employee_company_info SET direct_phone=@value WHERE employee_id=@id",
            "internal.companyMobile" => "UPDATE public.employee_company_info SET company_mobile=@value WHERE employee_id=@id",
            "internal.macAddress" => "UPDATE public.employee_company_info SET mac_address=@value WHERE employee_id=@id",
            "internal.branchCode" => "UPDATE public.employee_company_info SET branch_code=@value WHERE employee_id=@id",
            "internal.branchName" => "UPDATE public.employee_company_info SET branch_name=@value WHERE employee_id=@id",
            "internal.responsibilityProvince" => "UPDATE public.employee_company_info SET responsibility_province=@value WHERE employee_id=@id",
            "internal.checklistType" => "UPDATE public.employee_company_info SET checklist_type=@value WHERE employee_id=@id",
            "internal.productsResponsible" => "UPDATE public.employee_company_info SET products_responsible=@value WHERE employee_id=@id",
            "internal.workExperienceType" => "UPDATE public.employee_company_info SET work_experience_type=@value WHERE employee_id=@id",
            "internal.employeeStatus" => "UPDATE public.employee_company_info SET employee_status=@value WHERE employee_id=@id",
            "family.maritalStatus" => "UPDATE public.employee_family_info SET marital_status=@value WHERE employee_id=@id",
            "family.spouseName" => "UPDATE public.employee_family_info SET spouse_name=@value WHERE employee_id=@id",
            "family.spouseNationalId" => "UPDATE public.employee_family_info SET spouse_national_id=@value WHERE employee_id=@id",
            "family.phone" => "UPDATE public.employee_family_info SET family_phone=@value WHERE employee_id=@id",
            "family.currentAddressMapUrl" => "UPDATE public.employee_family_info SET current_address_map_url=@value WHERE employee_id=@id",
            _ => null
        };

        if (textSql is not null)
        {
            await using var command = new NpgsqlCommand(textSql, connection, transaction);
            command.Parameters.AddWithValue("id", employeeId);
            command.Parameters.Add("value", NpgsqlDbType.Text).Value =
                string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var typedSql = change.FieldKey switch
        {
            "personal.birthDate" => "UPDATE public.employee_personal_info SET birth_date=NULLIF(@value, '')::date WHERE employee_id=@id",
            "internal.startDate" => "UPDATE public.employee_company_info SET start_date=NULLIF(@value, '')::date WHERE employee_id=@id",
            "internal.appointmentDate" => "UPDATE public.employee_company_info SET appointment_date=NULLIF(@value, '')::date WHERE employee_id=@id",
            "internal.providentFundStartDate" => "UPDATE public.employee_company_info SET provident_fund_start_date=NULLIF(@value, '')::date WHERE employee_id=@id",
            "internal.hasCompanyParking" => "UPDATE public.employee_company_info SET has_company_parking=NULLIF(@value, '')::boolean WHERE employee_id=@id",
            "internal.excludeAttendance" => "UPDATE public.employee_company_info SET exclude_attendance_calculation=NULLIF(@value, '')::boolean WHERE employee_id=@id",
            _ => null
        };
        if (typedSql is not null)
        {
            await using var command = new NpgsqlCommand(typedSql, connection, transaction);
            command.Parameters.AddWithValue("id", employeeId);
            command.Parameters.AddWithValue("value", value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (change.FieldKey == "work.history")
        {
            await ReplaceWorkHistory(connection, transaction, employeeId,
                JsonSerializer.Deserialize<List<EmployeeWorkHistoryItem>>(value) ?? [], cancellationToken);
            return;
        }
        if (change.FieldKey == "education.history")
        {
            await ReplaceEducationHistory(connection, transaction, employeeId,
                JsonSerializer.Deserialize<List<EmployeeEducationItem>>(value) ?? [], cancellationToken);
            return;
        }
        if (change.FieldKey == "training.history")
        {
            await ReplaceTrainingHistory(connection, transaction, employeeId,
                JsonSerializer.Deserialize<List<EmployeeTrainingItem>>(value) ?? [], cancellationToken);
            return;
        }

        throw new InvalidOperationException($"Unsupported employee edit field: {change.FieldKey}");
    }

    private async Task<string?> ResolveAuthenticatedEmployeeId(CancellationToken cancellationToken)
    {
        var tenantId = User.FindFirst("tid")?.Value;
        var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;

        const string sql = """
            SELECT employee_id
            FROM public.microsoft_accounts
            WHERE tenant_id = @tenant_id
              AND entra_object_id = @object_id
              AND is_active = TRUE
            LIMIT 1
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("object_id", objectId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool IsValidProfileImage(string value) => string.IsNullOrWhiteSpace(value) || (value.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase) && value.Length<=3_000_000);
    private static string S(NpgsqlDataReader r,int i,string fallback="") => r.IsDBNull(i)?fallback:r.GetString(i);
    private static DateOnly? D(NpgsqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetFieldValue<DateOnly>(i);
    private static bool? B(NpgsqlDataReader r,int i)=>r.IsDBNull(i)?null:r.GetBoolean(i);
    private static int I(NpgsqlDataReader r,int i)=>r.IsDBNull(i)?0:r.GetInt32(i);
    private static decimal M(NpgsqlDataReader r,int i)=>r.IsDBNull(i)?0:r.GetDecimal(i);
    private static void Add(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static void AddDate(NpgsqlCommand command, string name, DateOnly? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Date).Value =
            value.HasValue ? value.Value : DBNull.Value;

    private static void AddBoolean(NpgsqlCommand command, string name, bool? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Boolean).Value =
            value.HasValue ? value.Value : DBNull.Value;
}
