using System.Text.Json;
using HrProject.Api.Controllers;
using HrProject.Shared.Models;

namespace HrProject.Api.Services;

public static class LotusNotesEmployeePayloadBuilder
{
    private const int WorkHistoryLimit = 14;
    private const int EducationHistoryLimit = 2;

    public static string Build(Employee employee)
    {
        // Keep the legacy Lotus field names isolated here so changes to its form do not
        // leak into the HR database model.
        var data = new Dictionary<string, object?>();
        Add(data, "Code_Emp", employee.EmployeeCode);
        Add(data, "TitleName", employee.Title);
        Add(data, "Name", employee.ThaiFullName);
        Add(data, "Name_eng", employee.EnglishFullName);
        Add(data, "NickName", employee.Nickname);
        Add(data, "IdNumber", employee.NationalId);
        Add(data, "Birthday", Date(employee.BirthDate));
        Add(data, "NameEng", employee.LotusNotesEmail);
        Add(data, "Mobile", employee.PersonalMobile);
        Add(data, "Tel", employee.HomePhone);
        Add(data, "BU", employee.BusinessUnit);
        Add(data, "Department", employee.Department);
        Add(data, "Position", employee.Position);
        Add(data, "Boss", employee.SupervisorName);
        Add(data, "Boss_Fnc", employee.FunctionalSupervisorName);
        Add(data, "Employ_Type", employee.EmploymentType);
        Add(data, "txtTimeWorkGroup", employee.WorkSchedule);
        Add(data, "Location", employee.WorkLocation);
        Add(data, "WorkDate", Date(employee.StartDate));
        Add(data, "PermDate", Date(employee.AppointmentDate));
        Add(data, "Fund", Date(employee.ProvidentFundStartDate));
        Add(data, "DealerBranch", employee.BranchCode);
        Add(data, "DealerBranchName", employee.BranchName);
        Add(data, "Ext", employee.InternalExtension);
        Add(data, "Direct_Ext", employee.DirectPhone);
        Add(data, "Office_mobile", employee.CompanyMobile);
        Add(data, "Buddy", employee.BuddyName);
        Add(data, "CurrentAddress", employee.CurrentAddress);
        Add(data, "Tumbol", employee.ResidenceSubdistrict);
        Add(data, "Ampur", employee.ResidenceDistrict);
        Add(data, "Province", employee.ResidenceProvince);
        Add(data, "Zipcode", employee.ResidencePostalCode);
        Add(data, "WorkPlace", employee.ResidenceProvince);
        Add(data, "Province_Resp", EmployeesController.JoinResponsibilityProvinces(
            employee.ResponsibilityProvinces, employee.ResponsibilityProvince));
        Add(data, "IdAddress", employee.IdCardAddress);
        Add(data, "CardAddress", employee.HouseRegistrationAddress);
        Add(data, "CheckListType", employee.ChecklistType);
        Add(data, "SubBU", employee.ProductsResponsible);
        Add(data, "MACAddr", employee.MacAddress);
        Add(data, "Prov", employee.CanTravelUpcountry);
        Add(data, "f_Parking", employee.HasCompanyParking);
        Add(data, "Religion", employee.Religion);
        Add(data, "BloodGr", employee.BloodType);
        Add(data, "WorkExp", employee.WorkExperienceType);
        Add(data, "Reference", employee.EmergencyContactName);
        Add(data, "RefTel", employee.EmergencyContactPhone);
        Add(data, "RefAddress", employee.EmergencyContactAddress);
        Add(data, "Status", employee.MaritalStatus);
        Add(data, "Law", employee.IsMarriageRegistered);
        Add(data, "name_marry", Join(employee.SpouseTitle, employee.SpouseName));
        Add(data, "MarryDate", Date(employee.MarriageDate));
        Add(data, "Income", employee.SpouseHasIncome);
        Add(data, "IdNumbermarry", employee.SpouseNationalId);
        Add(data, "PassPortID", employee.SpousePassportId);
        Add(data, "NamePassPort", employee.SpousePassportName);
        Add(data, "FilePassport", employee.SpousePassportFileName);
        Add(data, "KidNoStudy", employee.UneducatedChildCount);
        Add(data, "KidStudy", employee.StudyingChildCount);
        Add(data, "Insurance", employee.LifeInsuranceAmount);
        Add(data, "Map", employee.CurrentAddressMapUrl);

        AddWorkHistoryColumns(data, employee.WorkHistory);
        AddEducationColumns(data, employee.EducationHistory);
        AddTrainingColumns(data, employee.TrainingHistory);

        return JsonSerializer.Serialize(data);
    }

    public static string? BuildApprovedChanges(
        IReadOnlyCollection<EmployeeFieldChangeDto> changes,
        string resolvedThaiFullName)
    {
        var data = new Dictionary<string, object?>();
        foreach (var change in changes)
        {
            var value = change.NewValue?.Trim() ?? string.Empty;
            switch (change.FieldKey)
            {
                case "title": Set(data, "TitleName", value); break;
                case "firstName":
                case "lastName": Set(data, "Name", resolvedThaiFullName); break;
                case "thaiFullName": Set(data, "Name", value); break;
                case "englishFullName": Set(data, "Name_eng", value); break;
                case "nickname": Set(data, "NickName", value); break;
                case "lotusNotesEmail": Set(data, "NameEng", value); break;
                case "personalMobile": Set(data, "Mobile", value); break;
                case "homePhone": Set(data, "Tel", value); break;
                case "personal.nationalId": Set(data, "IdNumber", value); break;
                case "personal.birthDate": Set(data, "Birthday", value); break;
                case "personal.religion": Set(data, "Religion", value); break;
                case "personal.bloodType": Set(data, "BloodGr", value); break;
                case "personal.residenceProvince":
                    Set(data, "Province", value);
                    Set(data, "WorkPlace", value);
                    break;
                case "personal.residenceDistrict": Set(data, "Ampur", value); break;
                case "personal.residenceSubdistrict": Set(data, "Tumbol", value); break;
                case "personal.residencePostalCode": Set(data, "Zipcode", value); break;
                case "personal.currentAddress": Set(data, "CurrentAddress", value); break;
                case "personal.idCardAddress": Set(data, "IdAddress", value); break;
                case "personal.houseRegistrationAddress": Set(data, "CardAddress", value); break;
                case "personal.emergencyContactName": Set(data, "Reference", value); break;
                case "personal.emergencyContactPhone": Set(data, "RefTel", value); break;
                case "personal.emergencyContactAddress": Set(data, "RefAddress", value); break;
                case "internal.businessUnit": Set(data, "BU", value); break;
                case "internal.department": Set(data, "Department", value); break;
                case "internal.position": Set(data, "Position", value); break;
                case "internal.supervisor": Set(data, "Boss", value); break;
                case "internal.functionalSupervisor": Set(data, "Boss_Fnc", value); break;
                case "internal.buddy": Set(data, "Buddy", value); break;
                case "internal.employmentType": Set(data, "Employ_Type", value); break;
                case "internal.workSchedule": Set(data, "txtTimeWorkGroup", value); break;
                case "internal.workLocation": Set(data, "Location", value); break;
                case "internal.extension": Set(data, "Ext", value); break;
                case "internal.directPhone": Set(data, "Direct_Ext", value); break;
                case "internal.companyMobile": Set(data, "Office_mobile", value); break;
                case "internal.macAddress": Set(data, "MACAddr", value); break;
                case "internal.branchCode": Set(data, "DealerBranch", value); break;
                case "internal.branchName": Set(data, "DealerBranchName", value); break;
                case "internal.responsibilityProvince": Set(data, "Province_Resp", value); break;
                case "internal.checklistType": Set(data, "CheckListType", value); break;
                case "internal.productsResponsible": Set(data, "SubBU", value); break;
                case "internal.startDate": Set(data, "WorkDate", value); break;
                case "internal.appointmentDate": Set(data, "PermDate", value); break;
                case "internal.providentFundStartDate": Set(data, "Fund", value); break;
                case "internal.workExperienceType": Set(data, "WorkExp", value); break;
                case "internal.hasCompanyParking": Set(data, "f_Parking", BooleanValue(value)); break;
                case "family.maritalStatus": Set(data, "Status", value); break;
                case "family.spouseName": Set(data, "name_marry", value); break;
                case "family.spouseNationalId": Set(data, "IdNumbermarry", value); break;
                case "family.currentAddressMapUrl": Set(data, "Map", value); break;
                case "work.history": SetWorkHistoryColumns(data, Deserialize<EmployeeWorkHistoryItem>(value)); break;
                case "education.history": SetEducationColumns(data, Deserialize<EmployeeEducationItem>(value)); break;
                case "training.history": SetTrainingColumns(data, Deserialize<EmployeeTrainingItem>(value)); break;
            }
        }

        return data.Count == 0 ? null : JsonSerializer.Serialize(data);
    }

    private static void AddWorkHistoryColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeWorkHistoryItem> history)
    {
        for (var index = 0; index < history.Count; index++)
        {
            var suffix = index == 0 ? string.Empty : "_" + index.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var item = history[index];
            Add(data, $"RangeDate{suffix}", item.Period);
            Add(data, $"PositionHist{suffix}", item.Position);
            Add(data, $"Company{suffix}", item.Company);
        }
    }

    private static void AddEducationColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeEducationItem> history)
    {
        if (history.Count > 0)
        {
            Add(data, "Grade_1", history[0].Level);
            Add(data, "Collage_1", history[0].Institution);
            Add(data, "Major", history[0].Major);
            Add(data, "Finish_1", history[0].GraduationYear);
        }

        if (history.Count > 1)
        {
            Add(data, "Grade", history[1].Level);
            Add(data, "Collage", history[1].Institution);
            Add(data, "Major_1", history[1].Major);
            Add(data, "Finish", history[1].GraduationYear);
        }
    }

    private static readonly string[] TrainingSuffixes =
        ["", "_1", "_2", "_3", "_11", "_12", "_13", "_14", "_15", "_16", "_17", "_18"];

    private static void SetWorkHistoryColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeWorkHistoryItem> history)
    {
        for (var index = 0; index < WorkHistoryLimit; index++)
        {
            var suffix = index == 0 ? string.Empty : "_" + index.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var item = index < history.Count ? history[index] : null;
            Set(data, $"RangeDate{suffix}", item?.Period ?? string.Empty);
            Set(data, $"PositionHist{suffix}", item?.Position ?? string.Empty);
            Set(data, $"Company{suffix}", item?.Company ?? string.Empty);
        }
    }

    private static void SetEducationColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeEducationItem> history)
    {
        var first = history.Count > 0 ? history[0] : null;
        Set(data, "Grade_1", first?.Level ?? string.Empty);
        Set(data, "Collage_1", first?.Institution ?? string.Empty);
        Set(data, "Major", first?.Major ?? string.Empty);
        Set(data, "Finish_1", first?.GraduationYear ?? string.Empty);

        var second = history.Count > 1 ? history[1] : null;
        Set(data, "Grade", second?.Level ?? string.Empty);
        Set(data, "Collage", second?.Institution ?? string.Empty);
        Set(data, "Major_1", second?.Major ?? string.Empty);
        Set(data, "Finish", second?.GraduationYear ?? string.Empty);
    }

    private static void SetTrainingColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeTrainingItem> history)
    {
        for (var index = 0; index < TrainingSuffixes.Length; index++)
        {
            var suffix = TrainingSuffixes[index];
            var item = index < history.Count ? history[index] : null;
            Set(data, $"CourseDate{suffix}", item?.TrainingPeriod ?? string.Empty);
            Set(data, $"CourseName{suffix}", item?.CourseName ?? string.Empty);
            Set(data, $"CoursePlace{suffix}", item?.Location ?? string.Empty);
        }
    }

    private static List<T> Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<List<T>>(value, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? [];

    private static object BooleanValue(string value) =>
        bool.TryParse(value, out var parsed) ? parsed : value;

    private static void Set(IDictionary<string, object?> data, string field, object? value) =>
        data[field] = value;

    private static void AddTrainingColumns(
        IDictionary<string, object?> data,
        IReadOnlyList<EmployeeTrainingItem> history)
    {
        for (var index = 0; index < history.Count; index++)
        {
            var suffix = TrainingSuffixes[index];
            var item = history[index];
            Add(data, $"CourseDate{suffix}", item.TrainingPeriod);
            Add(data, $"CourseName{suffix}", item.CourseName);
            Add(data, $"CoursePlace{suffix}", item.Location);
            // Expense, Certificate and ExamFee are retained in HR, but the Notes form
            // has no matching writable fields in this section so they are not sent.
        }
    }

    private static void Add(IDictionary<string, object?> data, string field, object? value)
    {
        if (value is null) return;
        if (value is string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            data[field] = text.Trim();
            return;
        }

        // false and zero are meaningful values and must not be treated as empty.
        data[field] = value;
    }

    private static string? Date(DateOnly? value) =>
        !value.HasValue || value.Value == default ? null : value.Value.ToString("yyyy-MM-dd");

    private static string Join(params string?[] values) => string.Join(" ", values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim()));
}
