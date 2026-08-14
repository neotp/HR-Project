using System.Text.Json;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class EmployeesController(NpgsqlDataSource dataSource) : ControllerBase
{
    private const string BaseSelect = """
        SELECT e.id, e.employee_code,
               b.title, b.first_name_th, b.last_name_th, b.full_name_th,
               b.first_name_en, b.last_name_en, b.full_name_en, b.nickname,
               COALESCE(NULLIF(b.email_address, ''), b.email_alias, '') AS email,
               b.personal_mobile, b.home_phone, b.profile_image_data,
               c.company_name, c.company_code, c.business_unit, c.division,
               c.department, c.section_name, c.position_name, c.job_code,
               c.supervisor_name, c.leave_approver_name, c.functional_supervisor_name,
               c.buddy_name, c.employment_type, c.work_schedule, c.work_location,
               c.employee_status, c.internal_extension, c.direct_phone,
               c.company_mobile, c.mac_address, c.branch_code, c.branch_name,
               c.responsibility_province, c.checklist_type, c.products_responsible,
               c.start_date, c.appointment_date, c.provident_fund_start_date,
               c.work_experience_type, c.has_company_parking, c.can_travel_upcountry
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
        HasCompanyParking = B(reader, 43), CanTravelUpcountry = B(reader, 44)
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
            INSERT INTO public.employee_basic_info(employee_id,title,first_name_th,last_name_th,full_name_th,full_name_en,nickname,email_address,personal_mobile,home_phone,profile_image_data)
            VALUES(@id,@title,@first,@last,@thai,@english,@nickname,@email,@mobile,@home,@image)
            """;
        await using (var command = new NpgsqlCommand(basic,c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"title",e.Title); Add(command,"first",e.FirstName); Add(command,"last",e.LastName); Add(command,"thai",e.ThaiFullName); Add(command,"english",e.EnglishFullName); Add(command,"nickname",e.Nickname); Add(command,"email",e.Email); Add(command,"mobile",e.PersonalMobile); Add(command,"home",e.HomePhone); Add(command,"image",e.ProfileImageDataUrl); await command.ExecuteNonQueryAsync(token); }
        const string company = """
            INSERT INTO public.employee_company_info(employee_id,company_name,business_unit,division,department,section_name,position_name,job_code,supervisor_name,leave_approver_name,functional_supervisor_name,buddy_name,employment_type,work_schedule,work_location,employee_status,internal_extension,direct_phone,company_mobile,mac_address,branch_code,branch_name,responsibility_province,checklist_type,products_responsible,start_date,appointment_date,provident_fund_start_date,work_experience_type,has_company_parking,can_travel_upcountry)
            VALUES(@id,@company,@bu,@division,@department,@section,@position,@job,@supervisor,@approver,@functional,@buddy,@employment,@schedule,@location,@status,@extension,@direct,@company_mobile,@mac,@branch_code,@branch_name,@province,@checklist,@products,@start,@appointment,@fund,@experience,@parking,@travel)
            """;
        await using (var command = new NpgsqlCommand(company,c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"company",e.Company); Add(command,"bu",e.BusinessUnit); Add(command,"division",e.Division); Add(command,"department",e.Department); Add(command,"section",e.Section); Add(command,"position",e.Position); Add(command,"job",e.JobCode); Add(command,"supervisor",e.SupervisorName); Add(command,"approver",e.LeaveApproverName); Add(command,"functional",e.FunctionalSupervisorName); Add(command,"buddy",e.BuddyName); Add(command,"employment",e.EmploymentType); Add(command,"schedule",e.WorkSchedule); Add(command,"location",e.WorkLocation); Add(command,"status",e.EmployeeStatus); Add(command,"extension",e.InternalExtension); Add(command,"direct",e.DirectPhone); Add(command,"company_mobile",e.CompanyMobile); Add(command,"mac",e.MacAddress); Add(command,"branch_code",e.BranchCode); Add(command,"branch_name",e.BranchName); Add(command,"province",e.ResponsibilityProvince); Add(command,"checklist",e.ChecklistType); Add(command,"products",e.ProductsResponsible); AddDate(command,"start",e.StartDate==default?null:e.StartDate); AddDate(command,"appointment",e.AppointmentDate); AddDate(command,"fund",e.ProvidentFundStartDate); Add(command,"experience",e.WorkExperienceType); AddBoolean(command,"parking",e.HasCompanyParking); AddBoolean(command,"travel",e.CanTravelUpcountry); await command.ExecuteNonQueryAsync(token); }
        await using (var command = new NpgsqlCommand("INSERT INTO public.employee_personal_info(employee_id,religion,blood_type,residence_province,current_address,id_card_address,house_registration_address,emergency_contact_name,emergency_contact_phone,emergency_contact_address) VALUES(@id,@religion,@blood,@province,@current,@id_address,@house,@emergency,@phone,@emergency_address)",c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"religion",e.Religion); Add(command,"blood",e.BloodType); Add(command,"province",e.ResidenceProvince); Add(command,"current",e.CurrentAddress); Add(command,"id_address",e.IdCardAddress); Add(command,"house",e.HouseRegistrationAddress); Add(command,"emergency",e.EmergencyContactName); Add(command,"phone",e.EmergencyContactPhone); Add(command,"emergency_address",e.EmergencyContactAddress); await command.ExecuteNonQueryAsync(token); }
        await using (var command = new NpgsqlCommand("INSERT INTO public.employee_family_info(employee_id,marital_status,family_member_name,family_relationship,family_phone,family_occupation,current_address_map_url) VALUES(@id,@marital,@name,@relationship,@phone,@occupation,@map)",c,t)) { command.Parameters.AddWithValue("id",(long)e.Id); Add(command,"marital",e.MaritalStatus); Add(command,"name",e.FamilyMemberName); Add(command,"relationship",e.FamilyRelationship); Add(command,"phone",e.FamilyPhone); Add(command,"occupation",e.FamilyOccupation); Add(command,"map",e.CurrentAddressMapUrl); await command.ExecuteNonQueryAsync(token); }
    }

    private static async Task ReplaceHistories(NpgsqlConnection c, NpgsqlTransaction t, int id, Employee e, CancellationToken token)
    { await ReplaceWorkHistory(c,t,id,e.WorkHistory??[],token); await ReplaceEducationHistory(c,t,id,e.EducationHistory??[],token); await ReplaceTrainingHistory(c,t,id,e.TrainingHistory??[],token); }
    private static async Task ReplaceWorkHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeWorkHistoryItem> rows,CancellationToken token) { await Delete(c,t,"employee_work_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_work_history(employee_id,display_order,period_text,position_name,company_name) VALUES(@id,@order,@period,@position,@company)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"period",rows[i].Period); Add(q,"position",rows[i].Position); Add(q,"company",rows[i].Company); await q.ExecuteNonQueryAsync(token); } }
    private static async Task ReplaceEducationHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeEducationItem> rows,CancellationToken token) { await Delete(c,t,"employee_education_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_education_history(employee_id,display_order,education_level,institution_name,major_name,graduation_year) VALUES(@id,@order,@level,@institution,@major,@year)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"level",rows[i].Level); Add(q,"institution",rows[i].Institution); Add(q,"major",rows[i].Major); Add(q,"year",rows[i].GraduationYear); await q.ExecuteNonQueryAsync(token); } }
    private static async Task ReplaceTrainingHistory(NpgsqlConnection c,NpgsqlTransaction t,long id,IReadOnlyList<EmployeeTrainingItem> rows,CancellationToken token) { await Delete(c,t,"employee_training_history",id,token); for(var i=0;i<rows.Count;i++){ await using var q=new NpgsqlCommand("INSERT INTO public.employee_training_history(employee_id,display_order,course_name,training_period,location_name,expense,certificate,exam_fee) VALUES(@id,@order,@course,@period,@location,@expense,@certificate,@exam)",c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("order",i+1); Add(q,"course",rows[i].CourseName); Add(q,"period",rows[i].TrainingPeriod); Add(q,"location",rows[i].Location); q.Parameters.AddWithValue("expense",rows[i].Expense); Add(q,"certificate",rows[i].Certificate); q.Parameters.AddWithValue("exam",rows[i].ExamFee); await q.ExecuteNonQueryAsync(token); } }
    private static async Task Delete(NpgsqlConnection c,NpgsqlTransaction t,string table,long id,CancellationToken token){ await using var q=new NpgsqlCommand($"DELETE FROM public.{table} WHERE employee_id=@id",c,t); q.Parameters.AddWithValue("id",id); await q.ExecuteNonQueryAsync(token); }

    private static async Task ApplyApprovedChange(NpgsqlConnection c,NpgsqlTransaction t,long id,EmployeeFieldChangeDto change,CancellationToken token)
    {
        var value=change.NewValue.Trim(); string? sql=change.FieldKey switch { "title"=>"UPDATE public.employee_basic_info SET title=@value WHERE employee_id=@id", "firstName"=>"UPDATE public.employee_basic_info SET first_name_th=@value WHERE employee_id=@id", "lastName"=>"UPDATE public.employee_basic_info SET last_name_th=@value WHERE employee_id=@id", "thaiFullName"=>"UPDATE public.employee_basic_info SET full_name_th=@value WHERE employee_id=@id", "englishFullName"=>"UPDATE public.employee_basic_info SET full_name_en=@value WHERE employee_id=@id", "email"=>"UPDATE public.employee_basic_info SET email_address=@value WHERE employee_id=@id", "personal.religion"=>"UPDATE public.employee_personal_info SET religion=@value WHERE employee_id=@id", "personal.bloodType"=>"UPDATE public.employee_personal_info SET blood_type=@value WHERE employee_id=@id", "personal.residenceProvince"=>"UPDATE public.employee_personal_info SET residence_province=@value WHERE employee_id=@id", "personal.idCardAddress"=>"UPDATE public.employee_personal_info SET id_card_address=@value WHERE employee_id=@id", "personal.houseRegistrationAddress"=>"UPDATE public.employee_personal_info SET house_registration_address=@value WHERE employee_id=@id", "family.maritalStatus"=>"UPDATE public.employee_family_info SET marital_status=@value WHERE employee_id=@id", "family.spouseName"=>"UPDATE public.employee_family_info SET spouse_name=@value WHERE employee_id=@id", "family.spouseNationalId"=>"UPDATE public.employee_family_info SET spouse_national_id=@value WHERE employee_id=@id", _=>null };
        if(sql is not null){ await using var q=new NpgsqlCommand(sql,c,t); q.Parameters.AddWithValue("id",id); q.Parameters.AddWithValue("value",value); await q.ExecuteNonQueryAsync(token); return; }
        if(change.FieldKey=="work.history") await ReplaceWorkHistory(c,t,id,JsonSerializer.Deserialize<List<EmployeeWorkHistoryItem>>(value)??[],token);
        if(change.FieldKey=="education.history") await ReplaceEducationHistory(c,t,id,JsonSerializer.Deserialize<List<EmployeeEducationItem>>(value)??[],token);
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
