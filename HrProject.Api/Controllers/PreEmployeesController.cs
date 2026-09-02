using System.Text.Json;
using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/pre-employees")]
public sealed class PreEmployeesController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    PageActionPermissionService actionPermissionService) : ControllerBase
{
    private const string SelectColumns = """
        id, source_system, source_reference_id, employee_code, title, first_name_th,
        last_name_th, full_name_en, nickname, email_address, email_alias, personal_mobile,
        company_name, business_unit, department, position_name, start_date, supervisor_name,
        leave_approver_name, employment_type, work_location, status, validation_message,
        created_employee_id, imported_by, imported_by_name, imported_at, reviewed_by,
        reviewed_by_name, reviewed_at, created_by, created_by_name, created_at, updated_at,
        converted_by, converted_by_name, converted_at, employee_data
        """;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PreEmployeeDto>>> GetAll(CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "PRE_EMPLOYEES", cancellationToken))
            return Forbid();

        var result = new List<PreEmployeeDto>();
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM public.pre_employees ORDER BY CASE status WHEN 'READY' THEN 0 WHEN 'INCOMPLETE' THEN 1 WHEN 'DRAFT' THEN 2 ELSE 3 END, created_at DESC, id DESC");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return Ok(result);
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<PreEmployeeDto>> GetById(long id, CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await pageAccessService.HasAccess(actor.Value.EmployeeId, "PRE_EMPLOYEES", cancellationToken))
            return Forbid();

        var item = await Find(id, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<PreEmployeeDto>> Create(
        SavePreEmployeeRequest request, CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await Can(actor.Value.EmployeeId, "EDIT", cancellationToken)) return Forbid();

        var validation = Validate(request);
        const string sql = """
            INSERT INTO public.pre_employees
                (source_system, source_reference_id, employee_code, title, first_name_th,
                 last_name_th, full_name_en, nickname, email_address, email_alias, personal_mobile,
                 company_name, business_unit, department, position_name, start_date, supervisor_name,
                 leave_approver_name, employment_type, work_location, status, validation_message,
                 created_by, created_by_name, reviewed_by, reviewed_by_name, reviewed_at, employee_data)
            VALUES
                (@source_system, @source_reference_id, @employee_code, @title, @first_name_th,
                 @last_name_th, @full_name_en, @nickname, @email, @email_alias, @mobile,
                 @company, @bu, @department, @position, @start_date, @supervisor,
                 @approver, @employment_type, @work_location, @status, @validation_message,
                 @actor, @actor_name, @actor, @actor_name, CURRENT_TIMESTAMP, @employee_data::jsonb)
            RETURNING id
            """;
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            AddParameters(command, request, validation is null ? "READY" : "INCOMPLETE", validation);
            command.Parameters.AddWithValue("actor", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("actor_name", actor.Value.Name);
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            return Created($"api/pre-employees/{id}", await Find(id, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("รายการจากแหล่งข้อมูลนี้มีอยู่แล้ว");
        }
    }

    [HttpPut("{id:long}")]
    public async Task<ActionResult<PreEmployeeDto>> Update(
        long id, SavePreEmployeeRequest request, CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await Can(actor.Value.EmployeeId, "EDIT", cancellationToken)) return Forbid();
        var validation = Validate(request);
        const string sql = """
            UPDATE public.pre_employees SET
                source_system=@source_system, source_reference_id=@source_reference_id,
                employee_code=@employee_code, title=@title, first_name_th=@first_name_th,
                last_name_th=@last_name_th, full_name_en=@full_name_en, nickname=@nickname,
                email_address=@email, email_alias=@email_alias, personal_mobile=@mobile,
                company_name=@company, business_unit=@bu, department=@department,
                position_name=@position, start_date=@start_date, supervisor_name=@supervisor,
                leave_approver_name=@approver, employment_type=@employment_type,
                work_location=@work_location, status=@status, validation_message=@validation_message,
                employee_data=@employee_data::jsonb,
                reviewed_by=@actor, reviewed_by_name=@actor_name, reviewed_at=CURRENT_TIMESTAMP
            WHERE id=@id AND status NOT IN ('CONVERTED','CANCELLED')
            """;
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            AddParameters(command, request, validation is null ? "READY" : "INCOMPLETE", validation);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor.Value.EmployeeId);
            command.Parameters.AddWithValue("actor_name", actor.Value.Name);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return Conflict("รายการนี้สร้างพนักงานแล้วหรือไม่สามารถแก้ไขได้");
            return Ok(await Find(id, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("รายการจากแหล่งข้อมูลนี้มีอยู่แล้ว");
        }
    }

    [HttpPost("{id:long}/convert")]
    public async Task<ActionResult<ConvertPreEmployeeResult>> Convert(long id, CancellationToken cancellationToken)
    {
        var actor = await GetActor(cancellationToken);
        if (actor is null) return Unauthorized();
        if (!await Can(actor.Value.EmployeeId, "CONVERT", cancellationToken)) return Forbid();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            SavePreEmployeeRequest draft;
            string status;
            const string lockSql = """
                SELECT source_system, source_reference_id, employee_code, title, first_name_th,
                       last_name_th, full_name_en, nickname, email_address, email_alias,
                       personal_mobile, company_name, business_unit, department, position_name,
                       start_date, supervisor_name, leave_approver_name, employment_type,
                       work_location, status, employee_data
                FROM public.pre_employees WHERE id=@id FOR UPDATE
                """;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return NotFound();
                status = reader.GetString(20);
                var fallback = new SavePreEmployeeRequest(
                    S(reader,0), S(reader,1), S(reader,2), S(reader,3), S(reader,4), S(reader,5),
                    S(reader,6), S(reader,7), S(reader,8), S(reader,9), S(reader,10), S(reader,11),
                    S(reader,12), S(reader,13), S(reader,14), D(reader,15), S(reader,16), S(reader,17),
                    S(reader,18), S(reader,19));
                var employeeData = reader.IsDBNull(21)
                    ? null
                    : JsonSerializer.Deserialize<Employee>(reader.GetString(21), JsonOptions);
                draft = fallback with { EmployeeData = employeeData };
            }
            if (status == "CONVERTED") return Conflict("รายการนี้ถูกสร้างเป็นพนักงานแล้ว");
            var employee = ResolveEmployee(draft);
            var validation = ValidateEmployee(employee);
            if (validation is not null) return BadRequest(validation);

            const string duplicateSql = """
                SELECT EXISTS
                (
                    SELECT 1 FROM public.employees employee
                    LEFT JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
                    WHERE UPPER(employee.employee_code)=UPPER(@employee_code)
                       OR LOWER(COALESCE(basic.email_address,''))=LOWER(@email)
                )
                """;
            await using (var command = new NpgsqlCommand(duplicateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("employee_code", employee.EmployeeCode.Trim());
                command.Parameters.AddWithValue("email", employee.Email.Trim());
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                    return Conflict("รหัสพนักงานหรืออีเมลนี้มีอยู่ในระบบแล้ว");
            }

            long employeeId;
            await using (var command = new NpgsqlCommand("INSERT INTO public.employees(employee_code,is_active,source_system) VALUES(@code,TRUE,'PRE_EMPLOYEE') RETURNING id", connection, transaction))
            {
                command.Parameters.AddWithValue("code", employee.EmployeeCode.Trim());
                employeeId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            await InsertFullEmployeeData(connection, transaction, employeeId, employee, cancellationToken);
            const string finishSql = """
                UPDATE public.pre_employees SET status='CONVERTED', validation_message=NULL,
                    created_employee_id=@employee_id, converted_by=@actor,
                    converted_by_name=@actor_name, converted_at=CURRENT_TIMESTAMP
                WHERE id=@id
                """;
            await using (var command = new NpgsqlCommand(finishSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("employee_id", employeeId);
                command.Parameters.AddWithValue("actor", actor.Value.EmployeeId);
                command.Parameters.AddWithValue("actor_name", actor.Value.Name);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return Ok(new ConvertPreEmployeeResult(id, employeeId, employee.EmployeeCode.Trim()));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict("รหัสพนักงานหรืออีเมลนี้มีอยู่ในระบบแล้ว");
        }
    }

    private async Task<bool> Can(string employeeId, string action, CancellationToken token) =>
        await pageAccessService.HasAccess(employeeId, "PRE_EMPLOYEES", token) &&
        await actionPermissionService.HasPermission(employeeId, "PRE_EMPLOYEES", action, token);

    private async Task<PreEmployeeDto?> Find(long id, CancellationToken token)
    {
        await using var command = dataSource.CreateCommand($"SELECT {SelectColumns} FROM public.pre_employees WHERE id=@id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    private static string? Validate(SavePreEmployeeRequest request)
    {
        return ValidateEmployee(ResolveEmployee(request));
    }

    private static string? ValidateEmployee(Employee employee)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(employee.EmployeeCode)) missing.Add("รหัสพนักงาน");
        else if (employee.EmployeeCode.Trim().Length != 6) missing.Add("รหัสพนักงานต้องมี 6 ตัว");
        if (string.IsNullOrWhiteSpace(employee.FirstName)) missing.Add("ชื่อ");
        if (string.IsNullOrWhiteSpace(employee.LastName)) missing.Add("นามสกุล");
        if (string.IsNullOrWhiteSpace(employee.Email)) missing.Add("อีเมล");
        if (string.IsNullOrWhiteSpace(employee.Department)) missing.Add("แผนก");
        if (string.IsNullOrWhiteSpace(employee.Position)) missing.Add("ตำแหน่ง");
        return missing.Count == 0 ? null : $"ข้อมูลไม่ครบ: {string.Join(", ", missing)}";
    }

    private static void AddParameters(NpgsqlCommand command, SavePreEmployeeRequest request, string status, string? validation)
    {
        var employee = ResolveEmployee(request);
        AddText(command,"source_system",request.SourceSystem); AddText(command,"source_reference_id",request.SourceReferenceId);
        AddText(command,"employee_code",employee.EmployeeCode); AddText(command,"title",employee.Title);
        AddText(command,"first_name_th",employee.FirstName); AddText(command,"last_name_th",employee.LastName);
        AddText(command,"full_name_en",employee.EnglishFullName); AddText(command,"nickname",employee.Nickname);
        AddText(command,"email",employee.Email); AddText(command,"email_alias",employee.LotusNotesEmail);
        AddText(command,"mobile",employee.PersonalMobile); AddText(command,"company",employee.Company);
        AddText(command,"bu",employee.BusinessUnit); AddText(command,"department",employee.Department);
        AddText(command,"position",employee.Position); AddDate(command,"start_date",employee.StartDate==default?null:employee.StartDate);
        AddText(command,"supervisor",employee.SupervisorName); AddText(command,"approver",employee.LeaveApproverName);
        AddText(command,"employment_type",employee.EmploymentType); AddText(command,"work_location",employee.WorkLocation);
        command.Parameters.AddWithValue("status", status); AddText(command,"validation_message",validation);
        command.Parameters.AddWithValue("employee_data", JsonSerializer.Serialize(employee, JsonOptions));
    }

    private static PreEmployeeDto Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), S(r,1), S(r,2), S(r,3) ?? "", S(r,4) ?? "", S(r,5) ?? "",
        S(r,6) ?? "", S(r,7) ?? "", S(r,8) ?? "", S(r,9) ?? "", S(r,10) ?? "",
        S(r,11) ?? "", S(r,12) ?? "", S(r,13) ?? "", S(r,14) ?? "", S(r,15) ?? "",
        D(r,16), S(r,17) ?? "", S(r,18) ?? "", S(r,19) ?? "", S(r,20) ?? "",
        r.GetString(21), S(r,22), r.IsDBNull(23)?null:r.GetInt64(23), S(r,24), S(r,25),
        T(r,26), S(r,27), S(r,28), T(r,29), r.GetString(30), r.GetString(31),
        r.GetFieldValue<DateTimeOffset>(32), r.GetFieldValue<DateTimeOffset>(33), S(r,34), S(r,35), T(r,36),
        r.IsDBNull(37) ? new Employee() : JsonSerializer.Deserialize<Employee>(r.GetString(37), JsonOptions) ?? new Employee());

    private static Employee ResolveEmployee(SavePreEmployeeRequest request) => request.EmployeeData ?? new Employee
    {
        EmployeeCode=request.EmployeeCode??"", Title=request.Title??"", FirstName=request.FirstNameTh??"",
        LastName=request.LastNameTh??"", ThaiFullName=$"{request.FirstNameTh} {request.LastNameTh}".Trim(),
        EnglishFullName=request.FullNameEn??"", Nickname=request.Nickname??"", Email=request.Email??"",
        LotusNotesEmail=request.LotusNotesEmail??"", PersonalMobile=request.PersonalMobile??"",
        Company=request.Company??"", BusinessUnit=request.BusinessUnit??"", Department=request.Department??"",
        Position=request.Position??"", StartDate=request.StartDate??default, SupervisorName=request.SupervisorName??"",
        LeaveApproverName=request.LeaveApproverName??"", EmploymentType=request.EmploymentType??"",
        WorkLocation=request.WorkLocation??""
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task InsertFullEmployeeData(NpgsqlConnection c, NpgsqlTransaction t, long id, Employee e, CancellationToken token)
    {
        const string basic = """
            INSERT INTO public.employee_basic_info(employee_id,title,first_name_th,last_name_th,full_name_th,full_name_en,nickname,email_alias,email_address,personal_mobile,home_phone,profile_image_data)
            VALUES(@id,@title,@first,@last,@thai,@english,@nickname,@lotus,@email,@mobile,@home,@image)
            """;
        await using (var q=new NpgsqlCommand(basic,c,t)){q.Parameters.AddWithValue("id",id);AddText(q,"title",e.Title);AddText(q,"first",e.FirstName);AddText(q,"last",e.LastName);AddText(q,"thai",e.ThaiFullName);AddText(q,"english",e.EnglishFullName);AddText(q,"nickname",e.Nickname);AddText(q,"lotus",e.LotusNotesEmail);AddText(q,"email",e.Email);AddText(q,"mobile",e.PersonalMobile);AddText(q,"home",e.HomePhone);AddText(q,"image",e.ProfileImageDataUrl);await q.ExecuteNonQueryAsync(token);}
        const string company = """
            INSERT INTO public.employee_company_info(employee_id,company_name,business_unit,division,department,section_name,position_name,job_code,supervisor_name,leave_approver_name,functional_supervisor_name,buddy_name,employment_type,work_schedule,work_location,employee_status,internal_extension,direct_phone,company_mobile,mac_address,branch_code,branch_name,responsibility_province,checklist_type,products_responsible,start_date,appointment_date,provident_fund_start_date,work_experience_type,has_company_parking,can_travel_upcountry,exclude_attendance_calculation)
            VALUES(@id,@company,@bu,@division,@department,@section,@position,@job,@supervisor,@approver,@functional,@buddy,@employment,@schedule,@location,@status,@extension,@direct,@company_mobile,@mac,@branch_code,@branch_name,@province,@checklist,@products,@start,@appointment,@fund,@experience,@parking,@travel,@exclude)
            """;
        await using (var q=new NpgsqlCommand(company,c,t)){q.Parameters.AddWithValue("id",id);AddText(q,"company",e.Company);AddText(q,"bu",e.BusinessUnit);AddText(q,"division",e.Division);AddText(q,"department",e.Department);AddText(q,"section",e.Section);AddText(q,"position",e.Position);AddText(q,"job",e.JobCode);AddText(q,"supervisor",e.SupervisorName);AddText(q,"approver",e.LeaveApproverName);AddText(q,"functional",e.FunctionalSupervisorName);AddText(q,"buddy",e.BuddyName);AddText(q,"employment",e.EmploymentType);AddText(q,"schedule",e.WorkSchedule);AddText(q,"location",e.WorkLocation);AddText(q,"status",e.EmployeeStatus);AddText(q,"extension",e.InternalExtension);AddText(q,"direct",e.DirectPhone);AddText(q,"company_mobile",e.CompanyMobile);AddText(q,"mac",e.MacAddress);AddText(q,"branch_code",e.BranchCode);AddText(q,"branch_name",e.BranchName);AddText(q,"province",e.ResponsibilityProvince);AddText(q,"checklist",e.ChecklistType);AddText(q,"products",e.ProductsResponsible);AddDate(q,"start",e.StartDate==default?null:e.StartDate);AddDate(q,"appointment",e.AppointmentDate);AddDate(q,"fund",e.ProvidentFundStartDate);AddText(q,"experience",e.WorkExperienceType);AddBoolean(q,"parking",e.HasCompanyParking);AddBoolean(q,"travel",e.CanTravelUpcountry);q.Parameters.AddWithValue("exclude",e.ExcludeAttendanceCalculation);await q.ExecuteNonQueryAsync(token);}
        await using (var q=new NpgsqlCommand("INSERT INTO public.employee_personal_info(employee_id,religion,blood_type,residence_province,current_address,id_card_address,house_registration_address,emergency_contact_name,emergency_contact_phone,emergency_contact_address) VALUES(@id,@religion,@blood,@province,@current,@id_address,@house,@emergency,@phone,@emergency_address)",c,t)){q.Parameters.AddWithValue("id",id);AddText(q,"religion",e.Religion);AddText(q,"blood",e.BloodType);AddText(q,"province",e.ResidenceProvince);AddText(q,"current",e.CurrentAddress);AddText(q,"id_address",e.IdCardAddress);AddText(q,"house",e.HouseRegistrationAddress);AddText(q,"emergency",e.EmergencyContactName);AddText(q,"phone",e.EmergencyContactPhone);AddText(q,"emergency_address",e.EmergencyContactAddress);await q.ExecuteNonQueryAsync(token);}
        const string family = """
            INSERT INTO public.employee_family_info
                (employee_id,marital_status,is_marriage_registered,spouse_title,spouse_name,marriage_date,
                 spouse_has_income,spouse_national_id,spouse_passport_id,spouse_passport_name,
                 spouse_passport_file_name,uneducated_child_count,studying_child_count,life_insurance_amount,
                 parent_support_deduction_amount,spouse_parent_deduction_amount,family_member_name,
                 family_relationship,family_phone,family_occupation,current_address_map_url)
            VALUES(@id,@marital,@registered,@spouse_title,@spouse_name,@marriage_date,@spouse_income,
                   @spouse_national_id,@passport_id,@passport_name,@passport_file,@uneducated_children,
                   @studying_children,@life_insurance,@parent_deduction,@spouse_parent_deduction,
                   @family_name,@relationship,@family_phone,@occupation,@map)
            """;
        await using (var q=new NpgsqlCommand(family,c,t)){q.Parameters.AddWithValue("id",id);AddText(q,"marital",e.MaritalStatus);AddBoolean(q,"registered",e.IsMarriageRegistered);AddText(q,"spouse_title",e.SpouseTitle);AddText(q,"spouse_name",e.SpouseName);AddDate(q,"marriage_date",e.MarriageDate);AddBoolean(q,"spouse_income",e.SpouseHasIncome);AddText(q,"spouse_national_id",e.SpouseNationalId);AddText(q,"passport_id",e.SpousePassportId);AddText(q,"passport_name",e.SpousePassportName);AddText(q,"passport_file",e.SpousePassportFileName);q.Parameters.AddWithValue("uneducated_children",e.UneducatedChildCount);q.Parameters.AddWithValue("studying_children",e.StudyingChildCount);q.Parameters.AddWithValue("life_insurance",e.LifeInsuranceAmount);q.Parameters.AddWithValue("parent_deduction",e.ParentSupportDeductionAmount);q.Parameters.AddWithValue("spouse_parent_deduction",e.SpouseParentSupportDeductionAmount);AddText(q,"family_name",e.FamilyMemberName);AddText(q,"relationship",e.FamilyRelationship);AddText(q,"family_phone",e.FamilyPhone);AddText(q,"occupation",e.FamilyOccupation);AddText(q,"map",e.CurrentAddressMapUrl);await q.ExecuteNonQueryAsync(token);}
        for(var i=0;i<e.WorkHistory.Count;i++){var row=e.WorkHistory[i];await using var q=new NpgsqlCommand("INSERT INTO public.employee_work_history(employee_id,display_order,period_text,position_name,company_name) VALUES(@id,@order,@period,@position,@company)",c,t);q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("order",i+1);AddText(q,"period",row.Period);AddText(q,"position",row.Position);AddText(q,"company",row.Company);await q.ExecuteNonQueryAsync(token);}
        for(var i=0;i<e.EducationHistory.Count;i++){var row=e.EducationHistory[i];await using var q=new NpgsqlCommand("INSERT INTO public.employee_education_history(employee_id,display_order,education_level,institution_name,major_name,graduation_year) VALUES(@id,@order,@level,@institution,@major,@year)",c,t);q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("order",i+1);AddText(q,"level",row.Level);AddText(q,"institution",row.Institution);AddText(q,"major",row.Major);AddText(q,"year",row.GraduationYear);await q.ExecuteNonQueryAsync(token);}
        for(var i=0;i<e.TrainingHistory.Count;i++){var row=e.TrainingHistory[i];await using var q=new NpgsqlCommand("INSERT INTO public.employee_training_history(employee_id,display_order,course_name,training_period,location_name,expense,certificate,exam_fee) VALUES(@id,@order,@course,@period,@location,@expense,@certificate,@exam)",c,t);q.Parameters.AddWithValue("id",id);q.Parameters.AddWithValue("order",i+1);AddText(q,"course",row.CourseName);AddText(q,"period",row.TrainingPeriod);AddText(q,"location",row.Location);q.Parameters.AddWithValue("expense",row.Expense);AddText(q,"certificate",row.Certificate);q.Parameters.AddWithValue("exam",row.ExamFee);await q.ExecuteNonQueryAsync(token);}
    }

    private async Task<(string EmployeeId, string Name)?> GetActor(CancellationToken token)
    {
        var tenantId = User.FindFirst("tid")?.Value; var objectId = User.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId)) return null;
        await using var command = dataSource.CreateCommand("SELECT employee_id, COALESCE(NULLIF(display_name,''),employee_id) FROM public.microsoft_accounts WHERE tenant_id=@tenant AND entra_object_id=@object AND is_active=TRUE AND employee_id IS NOT NULL LIMIT 1");
        command.Parameters.AddWithValue("tenant",tenantId); command.Parameters.AddWithValue("object",objectId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0),reader.GetString(1)) : null;
    }

    private static string? S(NpgsqlDataReader r,int i) => r.IsDBNull(i)?null:r.GetString(i);
    private static DateOnly? D(NpgsqlDataReader r,int i) => r.IsDBNull(i)?null:r.GetFieldValue<DateOnly>(i);
    private static DateTimeOffset? T(NpgsqlDataReader r,int i) => r.IsDBNull(i)?null:r.GetFieldValue<DateTimeOffset>(i);
    private static void AddText(NpgsqlCommand c,string name,string? value) => c.Parameters.Add(name,NpgsqlDbType.Text).Value=string.IsNullOrWhiteSpace(value)?DBNull.Value:value.Trim();
    private static void AddDate(NpgsqlCommand c,string name,DateOnly? value) => c.Parameters.Add(name,NpgsqlDbType.Date).Value=value.HasValue?value.Value:DBNull.Value;
    private static void AddBoolean(NpgsqlCommand c,string name,bool? value) => c.Parameters.Add(name,NpgsqlDbType.Boolean).Value=value.HasValue?value.Value:DBNull.Value;
}
