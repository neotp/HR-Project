using HrProject.Api.Services;
using HrProject.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace HrProject.Api.Controllers;

[ApiController]
[Route("api/leave-documents/{documentId:long}/comments")]
public sealed class LeaveCommentsController(
    NpgsqlDataSource dataSource,
    PageAccessService pageAccessService,
    WorkflowEmailNotificationService workflowEmailNotificationService,
    LeaveCommentEmailService emailService,
    ILogger<LeaveCommentsController> logger) : ControllerBase
{
    [HttpGet("~/api/leave-comment-links/{commentId:long}")]
    public async Task<ActionResult<LeaveCommentLinkDestinationDto>> ResolveLink(
        long commentId,CancellationToken token)
    {
        var actor=await GetActor(token);
        if(actor is null)return Unauthorized();

        const string sql="""
            SELECT comment.leave_document_id,comment.author_employee_id,
                   document.creator_employee_id,document.status,
                   EXISTS(
                       SELECT 1 FROM public.leave_cancel_requests request
                       WHERE request.leave_document_id=document.id AND request.status='PENDING')
            FROM public.leave_document_comments comment
            JOIN public.leave_documents document ON document.id=comment.leave_document_id
            WHERE comment.id=@comment_id
            """;
        long documentId;
        string authorId;
        string ownerId;
        string status;
        bool hasPendingCancel;
        await using(var command=dataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("comment_id",commentId);
            await using var reader=await command.ExecuteReaderAsync(token);
            if(!await reader.ReadAsync(token))return NotFound();
            documentId=reader.GetInt64(0);
            authorId=reader.GetString(1);
            ownerId=reader.GetString(2);
            status=reader.GetString(3);
            hasPendingCancel=reader.GetBoolean(4);
        }

        var query=$"?documentId={documentId}&openComments=true";
        string route;
        if((status=="EDIT_REQUESTED"||hasPendingCancel)&&
           await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_REVISIONS",token))
        {
            route="/leave/revisions";
        }
        else if(status=="PENDING_APPROVAL"&&
                string.Equals(authorId,ownerId,StringComparison.OrdinalIgnoreCase)&&
                await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_PENDING",token))
        {
            var document=await GetDocument(documentId,token);
            route=document is not null&&await CanComment(document,actor.Value.EmployeeId,token)
                ? "/leave/pending"
                : "/leave/all-documents";
        }
        else if(string.Equals(actor.Value.EmployeeId,ownerId,StringComparison.OrdinalIgnoreCase))
        {
            route="/leave/documents";
        }
        else
        {
            route="/leave/all-documents";
        }

        return Ok(new LeaveCommentLinkDestinationDto(route+query));
    }

    [HttpGet("{commentId:long}/attachments/{attachmentId:long}/preview")]
    public async Task<IActionResult> PreviewAttachment(
        long documentId,long commentId,long attachmentId,CancellationToken token)
    {
        var actor=await GetActor(token);
        if(actor is null)return Unauthorized();
        var document=await GetDocument(documentId,token);
        if(document is null)return NotFound();
        var canView=await CanComment(document,actor.Value.EmployeeId,token)||
            await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_ALL_DOCUMENTS",token)||
            await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_REVISIONS",token);
        if(!canView)return Forbid();

        const string sql="""
            SELECT attachment.content_type,attachment.file_content
            FROM public.leave_comment_attachments attachment
            JOIN public.leave_document_comments comment ON comment.id=attachment.leave_comment_id
            WHERE attachment.id=@attachment_id AND comment.id=@comment_id
              AND comment.leave_document_id=@document_id
            """;
        await using var command=dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("attachment_id",attachmentId);
        command.Parameters.AddWithValue("comment_id",commentId);
        command.Parameters.AddWithValue("document_id",documentId);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return NotFound();
        return File((byte[])reader[1],reader.GetString(0));
    }

    [HttpGet]
    public async Task<ActionResult<LeaveCommentContextDto>> Get(long documentId,CancellationToken token)
    {
        var actor=await GetActor(token);
        if(actor is null)return Unauthorized();
        var document=await GetDocument(documentId,token);
        if(document is null)return NotFound();
        var canComment=await CanComment(document,actor.Value.EmployeeId,token);
        if(!canComment&&
           !await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_ALL_DOCUMENTS",token)&&
           !await pageAccessService.HasAccess(actor.Value.EmployeeId,"LEAVE_REVISIONS",token))return Forbid();

        const string commentsSql="""
            SELECT id,author_employee_id,author_name,author_email,comment_text,created_at
            FROM public.leave_document_comments
            WHERE leave_document_id=@document_id
            ORDER BY created_at,id
            """;
        var comments=new List<CommentRow>();
        await using(var command=dataSource.CreateCommand(commentsSql))
        {
            command.Parameters.AddWithValue("document_id",documentId);
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))comments.Add(new CommentRow(
                reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
                reader.IsDBNull(3)?null:reader.GetString(3),reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }

        var recipientsByComment=new Dictionary<long,List<LeaveCommentRecipientDto>>();
        var attachmentsByComment=new Dictionary<long,List<LeaveCommentAttachmentDto>>();
        if(comments.Count>0)
        {
            const string recipientsSql="""
                SELECT leave_comment_id,recipient_employee_id,recipient_name,recipient_email,
                       recipient_type<>'ADDITIONAL'
                FROM public.leave_comment_notifications
                WHERE leave_comment_id=ANY(@comment_ids)
                ORDER BY id
                """;
            await using var command=dataSource.CreateCommand(recipientsSql);
            command.Parameters.AddWithValue("comment_ids",comments.Select(item=>item.Id).ToArray());
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var commentId=reader.GetInt64(0);
                if(!recipientsByComment.TryGetValue(commentId,out var recipients))
                    recipientsByComment[commentId]=recipients=[];
                recipients.Add(new LeaveCommentRecipientDto(
                    reader.IsDBNull(1)?null:reader.GetString(1),reader.GetString(2),
                    reader.GetString(3),reader.GetBoolean(4)));
            }

            const string attachmentsSql="""
                SELECT leave_comment_id,id,original_file_name,content_type,file_size_bytes,uploaded_at
                FROM public.leave_comment_attachments
                WHERE leave_comment_id=ANY(@comment_ids)
                ORDER BY leave_comment_id,uploaded_at,id
                """;
            await using var attachmentCommand=dataSource.CreateCommand(attachmentsSql);
            attachmentCommand.Parameters.AddWithValue("comment_ids",comments.Select(item=>item.Id).ToArray());
            await using var attachmentReader=await attachmentCommand.ExecuteReaderAsync(token);
            while(await attachmentReader.ReadAsync(token))
            {
                var commentId=attachmentReader.GetInt64(0);
                if(!attachmentsByComment.TryGetValue(commentId,out var attachments))
                    attachmentsByComment[commentId]=attachments=[];
                attachments.Add(new LeaveCommentAttachmentDto(
                    attachmentReader.GetInt64(1),attachmentReader.GetString(2),
                    attachmentReader.GetString(3),attachmentReader.GetInt64(4),
                    attachmentReader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var result=comments.Select(item=>new LeaveDocumentCommentDto(
            item.Id,documentId,item.AuthorEmployeeId,item.AuthorName,item.AuthorEmail,
            item.Text,item.CreatedAt,recipientsByComment.GetValueOrDefault(item.Id)??[],
            attachmentsByComment.GetValueOrDefault(item.Id)??[])).ToList();
        var defaultRecipients=DefaultRecipients(document).ToList();
        foreach(var recipient in await GetRevisionNotificationRecipients(documentId,token))
        {
            if(!defaultRecipients.Any(item=>string.Equals(item.Email,recipient.Email,StringComparison.OrdinalIgnoreCase)))
                defaultRecipients.Add(new LeaveCommentRecipientDto(
                    recipient.EmployeeId,recipient.Name,recipient.Email,true));
        }
        const string participantsSql="""
            SELECT employee_id,employee_name,employee_email
            FROM public.leave_comment_participants
            WHERE leave_document_id=@document_id
            ORDER BY employee_name,employee_id
            """;
        await using(var command=dataSource.CreateCommand(participantsSql))
        {
            command.Parameters.AddWithValue("document_id",documentId);
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var email=reader.GetString(2);
                if(!defaultRecipients.Any(item=>string.Equals(item.Email,email,StringComparison.OrdinalIgnoreCase)))
                    defaultRecipients.Add(new LeaveCommentRecipientDto(
                        reader.GetString(0),reader.GetString(1),email,false));
            }
        }
        return Ok(new LeaveCommentContextDto(result,defaultRecipients,canComment));
    }

    [HttpPost]
    public async Task<ActionResult<LeaveDocumentCommentDto>> Create(
        long documentId,CreateLeaveCommentRequest request,CancellationToken token)
    {
        var text=request.CommentText?.Trim();
        if(string.IsNullOrWhiteSpace(text))return BadRequest("กรุณากรอกความคิดเห็น");
        if(text.Length>4000)return BadRequest("ความคิดเห็นต้องไม่เกิน 4,000 ตัวอักษร");
        var attachmentError=ValidateAttachments(request.Attachments);
        if(attachmentError is not null)return BadRequest(attachmentError);
        var additionalIds=(request.AdditionalRecipientEmployeeIds??[])
            .Where(value=>!string.IsNullOrWhiteSpace(value)).Select(value=>value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(additionalIds.Length>20)return BadRequest("เลือกผู้รับเพิ่มเติมได้ไม่เกิน 20 คน");

        var actor=await GetActor(token);
        if(actor is null)return Unauthorized();
        var document=await GetDocument(documentId,token);
        if(document is null)return NotFound();
        if(!await CanComment(document,actor.Value.EmployeeId,token))return Forbid();

        var recipients=new Dictionary<string,Recipient>(StringComparer.OrdinalIgnoreCase);
        AddRecipient(recipients,new Recipient(document.CreatorEmployeeId,document.CreatorName,
            document.CreatorEmail,"REQUESTER"),actor.Value.EmployeeId);
        AddRecipient(recipients,new Recipient(document.BossEmployeeId,
            document.BossName??document.BossEmployeeId??"Boss",
            document.BossEmail,"BOSS"),actor.Value.EmployeeId);
        AddRecipient(recipients,new Recipient(document.LeaveApproverEmployeeId,
            document.LeaveApproverName??document.LeaveApproverEmployeeId??"Reporting To (Leave Approve)",
            document.LeaveApproverEmail,"LEAVE_APPROVER"),actor.Value.EmployeeId);
        AddRecipient(recipients,new Recipient(document.ApproverEmployeeId,document.ApproverName,
            document.ApproverEmail,"APPROVER"),actor.Value.EmployeeId);
        foreach(var recipient in await GetRevisionNotificationRecipients(documentId,token))
            AddRecipient(recipients,new Recipient(recipient.EmployeeId,recipient.Name,
                recipient.Email,"WORKFLOW"),actor.Value.EmployeeId);
        var selectedParticipants=new List<Recipient>();
        if(additionalIds.Length>0)
        {
            const string sql="""
                SELECT employee.employee_code,
                       COALESCE(NULLIF(basic.full_name_th,''),NULLIF(basic.full_name_en,''),employee.employee_code),
                       NULLIF(BTRIM(basic.email_address),'')
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
                WHERE employee.is_active=TRUE AND employee.employee_code=ANY(@employee_ids)
                """;
            await using var command=dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("employee_ids",additionalIds);
            await using var reader=await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var participant=new Recipient(reader.GetString(0),reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetString(2),"ADDITIONAL");
                if(!string.IsNullOrWhiteSpace(participant.Email))selectedParticipants.Add(participant);
                AddRecipient(recipients,participant,actor.Value.EmployeeId);
            }
        }

        long commentId;
        var createdAt=DateTimeOffset.UtcNow;
        var createdAttachments=new List<LeaveCommentAttachmentDto>();
        await using(var connection=await dataSource.OpenConnectionAsync(token))
        await using(var transaction=await connection.BeginTransactionAsync(token))
        {
            const string deleteParticipants="""
                DELETE FROM public.leave_comment_participants
                WHERE leave_document_id=@document_id
                  AND (CARDINALITY(@employee_ids)=0 OR NOT (employee_id=ANY(@employee_ids)))
                """;
            await using(var command=new NpgsqlCommand(deleteParticipants,connection,transaction))
            {
                command.Parameters.AddWithValue("document_id",documentId);
                command.Parameters.AddWithValue("employee_ids",selectedParticipants.Select(item=>item.EmployeeId!).ToArray());
                await command.ExecuteNonQueryAsync(token);
            }
            const string upsertParticipant="""
                INSERT INTO public.leave_comment_participants
                    (leave_document_id,employee_id,employee_name,employee_email,added_by)
                VALUES(@document_id,@employee_id,@name,@email,@added_by)
                ON CONFLICT(leave_document_id,employee_id) DO UPDATE SET
                    employee_name=EXCLUDED.employee_name,employee_email=EXCLUDED.employee_email,
                    updated_at=CURRENT_TIMESTAMP
                """;
            foreach(var participant in selectedParticipants)
            {
                await using var command=new NpgsqlCommand(upsertParticipant,connection,transaction);
                command.Parameters.AddWithValue("document_id",documentId);
                command.Parameters.AddWithValue("employee_id",participant.EmployeeId!);
                command.Parameters.AddWithValue("name",participant.Name);
                command.Parameters.AddWithValue("email",participant.Email!);
                command.Parameters.AddWithValue("added_by",actor.Value.EmployeeId);
                await command.ExecuteNonQueryAsync(token);
            }
            const string insertComment="""
                INSERT INTO public.leave_document_comments
                    (leave_document_id,author_employee_id,author_name,author_email,comment_text)
                VALUES(@document_id,@author_id,@author_name,@author_email,@text)
                RETURNING id,created_at
                """;
            await using(var command=new NpgsqlCommand(insertComment,connection,transaction))
            {
                command.Parameters.AddWithValue("document_id",documentId);
                command.Parameters.AddWithValue("author_id",actor.Value.EmployeeId);
                command.Parameters.AddWithValue("author_name",actor.Value.Name);
                command.Parameters.Add(new NpgsqlParameter<string?>("author_email",actor.Value.Email));
                command.Parameters.AddWithValue("text",text);
                await using var reader=await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token); commentId=reader.GetInt64(0);
                createdAt=reader.GetFieldValue<DateTimeOffset>(1);
            }
            const string insertAttachment="""
                INSERT INTO public.leave_comment_attachments
                    (leave_comment_id,original_file_name,content_type,file_size_bytes,file_content)
                VALUES(@comment_id,@file_name,@content_type,@file_size,@content)
                RETURNING id,uploaded_at
                """;
            foreach(var attachment in request.Attachments??[])
            {
                await using var command=new NpgsqlCommand(insertAttachment,connection,transaction);
                command.Parameters.AddWithValue("comment_id",commentId);
                command.Parameters.AddWithValue("file_name",Path.GetFileName(attachment.FileName));
                command.Parameters.AddWithValue("content_type",attachment.ContentType.ToLowerInvariant());
                command.Parameters.AddWithValue("file_size",(long)attachment.Content.Length);
                command.Parameters.Add("content",NpgsqlTypes.NpgsqlDbType.Bytea).Value=attachment.Content;
                await using var reader=await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                createdAttachments.Add(new LeaveCommentAttachmentDto(
                    reader.GetInt64(0),Path.GetFileName(attachment.FileName),
                    attachment.ContentType.ToLowerInvariant(),attachment.Content.LongLength,
                    reader.GetFieldValue<DateTimeOffset>(1)));
            }
            const string insertRecipient="""
                INSERT INTO public.leave_comment_notifications
                    (leave_comment_id,recipient_employee_id,recipient_name,recipient_email,recipient_type)
                VALUES(@comment_id,@employee_id,@name,@email,@type)
                """;
            foreach(var recipient in recipients.Values)
            {
                await using var command=new NpgsqlCommand(insertRecipient,connection,transaction);
                command.Parameters.AddWithValue("comment_id",commentId);
                command.Parameters.Add(new NpgsqlParameter<string?>("employee_id",recipient.EmployeeId));
                command.Parameters.AddWithValue("name",recipient.Name);
                command.Parameters.AddWithValue("email",recipient.Email!);
                command.Parameters.AddWithValue("type",recipient.Type);
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }

        if(recipients.Count>0)
        {
            try{await emailService.SendAsync(commentId,token);}
            catch(Exception exception){logger.LogWarning(exception,
                "Comment {CommentId} was saved but email delivery is queued for retry",commentId);}
        }
        var recipientDtos=recipients.Values.Select(ToDto).ToList();
        return Created($"api/leave-documents/{documentId}/comments/{commentId}",
            new LeaveDocumentCommentDto(commentId,documentId,actor.Value.EmployeeId,actor.Value.Name,
                actor.Value.Email,text,createdAt,recipientDtos,createdAttachments));
    }

    private static void AddRecipient(Dictionary<string,Recipient> recipients,Recipient recipient,string authorId)
    {
        if(string.IsNullOrWhiteSpace(recipient.Email)||
           string.Equals(recipient.EmployeeId,authorId,StringComparison.OrdinalIgnoreCase))return;
        recipients.TryAdd(recipient.Email.Trim(),recipient with{Email=recipient.Email.Trim()});
    }

    private async Task<IReadOnlyList<WorkflowEmailRecipient>> GetRevisionNotificationRecipients(
        long documentId,CancellationToken token)
    {
        const string sql="""
            SELECT EXISTS(
                SELECT 1
                FROM public.leave_documents document
                WHERE document.id=@document_id
                  AND (document.status='EDIT_REQUESTED' OR EXISTS(
                      SELECT 1 FROM public.leave_cancel_requests request
                      WHERE request.leave_document_id=document.id AND request.status='PENDING')))
            """;
        await using var command=dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("document_id",documentId);
        if(!((bool)(await command.ExecuteScalarAsync(token))!))return [];
        return await workflowEmailNotificationService.GetRecipientsAsync(
            "LEAVE_REVISIONS",null,token);
    }
    private static string? ValidateAttachments(IReadOnlyList<LeaveCommentAttachmentUploadDto>? attachments)
    {
        if(attachments is null||attachments.Count==0)return null;
        if(attachments.Count>5)return "แนบรูปได้ไม่เกิน 5 รูปต่อ Comment";
        const int maxSize=3*1024*1024;
        var accepted=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {"image/png","image/jpeg","image/webp"};
        foreach(var attachment in attachments)
        {
            var fileName=Path.GetFileName(attachment.FileName);
            if(string.IsNullOrWhiteSpace(fileName))return "ชื่อไฟล์รูปไม่ถูกต้อง";
            if(!accepted.Contains(attachment.ContentType))
                return $"ไฟล์ {fileName} ไม่รองรับ รองรับเฉพาะ PNG, JPG และ WEBP";
            if(attachment.Content is null||attachment.Content.Length==0||attachment.Content.Length>maxSize)
                return $"ไฟล์ {fileName} ต้องมีขนาดมากกว่า 0 และไม่เกิน 3 MB";
        }
        return null;
    }

    private static LeaveCommentRecipientDto ToDto(Recipient item)=>new(
        item.EmployeeId,item.Name,item.Email??string.Empty,item.Type!="ADDITIONAL");
    private static IReadOnlyList<LeaveCommentRecipientDto> DefaultRecipients(Document item)
    {
        var result=new List<LeaveCommentRecipientDto>();
        AddDefaultRecipient(result,item.CreatorEmployeeId,item.CreatorName,item.CreatorEmail);
        AddDefaultRecipient(result,item.BossEmployeeId,item.BossName,item.BossEmail);
        AddDefaultRecipient(result,item.LeaveApproverEmployeeId,item.LeaveApproverName,item.LeaveApproverEmail);
        AddDefaultRecipient(result,item.ApproverEmployeeId,item.ApproverName,item.ApproverEmail);
        return result;
    }

    private static void AddDefaultRecipient(List<LeaveCommentRecipientDto> recipients,
        string? employeeId,string? name,string? email)
    {
        if(string.IsNullOrWhiteSpace(email)||
           recipients.Any(value=>string.Equals(value.Email,email,StringComparison.OrdinalIgnoreCase)))return;
        recipients.Add(new LeaveCommentRecipientDto(employeeId,
            string.IsNullOrWhiteSpace(name)?employeeId??email:name,email.Trim(),true));
    }

    private async Task<bool> CanComment(Document document,string actorId,CancellationToken token)
    {
        if(string.Equals(document.CreatorEmployeeId,actorId,StringComparison.OrdinalIgnoreCase)||
           string.Equals(document.ApproverEmployeeId,actorId,StringComparison.OrdinalIgnoreCase))return true;
        const string sql="""
            SELECT EXISTS(
                SELECT 1
                FROM public.leave_documents document
                JOIN public.employees owner ON owner.employee_code=document.creator_employee_id
                JOIN public.employee_company_info company ON company.employee_id=owner.id
                JOIN public.employees actor ON actor.employee_code=@actor_id AND actor.is_active=TRUE
                JOIN public.employee_basic_info basic ON basic.employee_id=actor.id
                WHERE document.id=@document_id AND
                  (UPPER(BTRIM(COALESCE(company.supervisor_employee_id,'')))=UPPER(actor.employee_code)
                   OR UPPER(BTRIM(COALESCE(company.leave_approver_employee_id,'')))=UPPER(actor.employee_code)
                   OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.supervisor_name,''))),'\s+',' ','g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_th,basic.last_name_th))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_en,basic.last_name_en))),'\s+',' ','g'))
                   OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(company.leave_approver_name,''))),'\s+',' ','g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_th,basic.last_name_th))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_en,basic.last_name_en))),'\s+',' ','g')))
            )
            """;
        await using var command=dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("actor_id",actorId);command.Parameters.AddWithValue("document_id",document.Id);
        if((bool)(await command.ExecuteScalarAsync(token))!)return true;

        const string participantSql="""
            SELECT EXISTS(
                SELECT 1 FROM public.leave_comment_participants
                WHERE leave_document_id=@document_id
                  AND UPPER(BTRIM(employee_id))=UPPER(BTRIM(@actor_id)))
            """;
        await using var participantCommand=dataSource.CreateCommand(participantSql);
        participantCommand.Parameters.AddWithValue("document_id",document.Id);
        participantCommand.Parameters.AddWithValue("actor_id",actorId);
        if((bool)(await participantCommand.ExecuteScalarAsync(token))!)return true;
        return await pageAccessService.HasAccess(actorId,"LEAVE_REVISIONS",token);
    }

    private async Task<Document?> GetDocument(long id,CancellationToken token)
    {
        const string sql="""
            SELECT document.id,document.creator_employee_id,document.creator_name,
                   creator_basic.email_address,document.approver_employee_id,document.approver_name,
                   approver_basic.email_address,
                   boss.employee_code,boss.full_name,boss.email_address,
                   reporting.employee_code,reporting.full_name,reporting.email_address
            FROM public.leave_documents document
            LEFT JOIN public.employees creator ON creator.employee_code=document.creator_employee_id
            LEFT JOIN public.employee_basic_info creator_basic ON creator_basic.employee_id=creator.id
            LEFT JOIN public.employee_company_info creator_company ON creator_company.employee_id=creator.id
            LEFT JOIN public.employees approver ON approver.employee_code=document.approver_employee_id
            LEFT JOIN public.employee_basic_info approver_basic ON approver_basic.employee_id=approver.id
            LEFT JOIN LATERAL
            (
                SELECT employee.employee_code,
                       COALESCE(NULLIF(basic.full_name_th,''),NULLIF(basic.full_name_en,''),employee.employee_code) AS full_name,
                       NULLIF(BTRIM(basic.email_address),'') AS email_address
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
                WHERE employee.is_active=TRUE AND
                  (UPPER(BTRIM(COALESCE(creator_company.supervisor_employee_id,'')))=UPPER(employee.employee_code)
                   OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.supervisor_name,''))),'\s+',' ','g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_th,basic.last_name_th))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_en,basic.last_name_en))),'\s+',' ','g')))
                ORDER BY CASE WHEN UPPER(BTRIM(COALESCE(creator_company.supervisor_employee_id,'')))=UPPER(employee.employee_code) THEN 0 ELSE 1 END,
                         employee.id
                LIMIT 1
            ) boss ON TRUE
            LEFT JOIN LATERAL
            (
                SELECT employee.employee_code,
                       COALESCE(NULLIF(basic.full_name_th,''),NULLIF(basic.full_name_en,''),employee.employee_code) AS full_name,
                       NULLIF(BTRIM(basic.email_address),'') AS email_address
                FROM public.employees employee
                JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
                WHERE employee.is_active=TRUE AND
                  (UPPER(BTRIM(COALESCE(creator_company.leave_approver_employee_id,'')))=UPPER(employee.employee_code)
                   OR REGEXP_REPLACE(UPPER(BTRIM(COALESCE(creator_company.leave_approver_name,''))),'\s+',' ','g') IN
                      (REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_th,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(COALESCE(basic.full_name_en,''))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_th,basic.last_name_th))),'\s+',' ','g'),
                       REGEXP_REPLACE(UPPER(BTRIM(CONCAT_WS(' ',basic.first_name_en,basic.last_name_en))),'\s+',' ','g')))
                ORDER BY CASE WHEN UPPER(BTRIM(COALESCE(creator_company.leave_approver_employee_id,'')))=UPPER(employee.employee_code) THEN 0 ELSE 1 END,
                         employee.id
                LIMIT 1
            ) reporting ON TRUE
            WHERE document.id=@id
            """;
        await using var command=dataSource.CreateCommand(sql);command.Parameters.AddWithValue("id",id);
        await using var reader=await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)?new Document(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
            reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4),reader.GetString(5),
            reader.IsDBNull(6)?null:reader.GetString(6),
            reader.IsDBNull(7)?null:reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9),
            reader.IsDBNull(10)?null:reader.GetString(10),reader.IsDBNull(11)?null:reader.GetString(11),reader.IsDBNull(12)?null:reader.GetString(12)):null;
    }
    private async Task<(string EmployeeId,string Name,string? Email)?> GetActor(CancellationToken token)
    {
        var employeeId=User.FindFirst("employee_id")?.Value;
        if(string.IsNullOrWhiteSpace(employeeId))
        {
            var tenant=User.FindFirst("tid")?.Value;var oid=User.FindFirst("oid")?.Value;
            if(string.IsNullOrWhiteSpace(tenant)||string.IsNullOrWhiteSpace(oid))return null;
            await using var account=dataSource.CreateCommand("SELECT employee_id FROM public.microsoft_accounts WHERE tenant_id=@tenant AND entra_object_id=@oid AND is_active=TRUE LIMIT 1");
            account.Parameters.AddWithValue("tenant",tenant);account.Parameters.AddWithValue("oid",oid);
            employeeId=(string?)await account.ExecuteScalarAsync(token);
        }
        if(string.IsNullOrWhiteSpace(employeeId))return null;
        const string sql="""
            SELECT COALESCE(NULLIF(basic.full_name_th,''),NULLIF(basic.full_name_en,''),employee.employee_code),
                   NULLIF(BTRIM(basic.email_address),'')
            FROM public.employees employee JOIN public.employee_basic_info basic ON basic.employee_id=employee.id
            WHERE employee.employee_code=@id LIMIT 1
            """;
        await using var command=dataSource.CreateCommand(sql);command.Parameters.AddWithValue("id",employeeId);
        await using var reader=await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)?(employeeId,reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1)):null;
    }
    private sealed record Document(long Id,string CreatorEmployeeId,string CreatorName,string? CreatorEmail,
        string? ApproverEmployeeId,string ApproverName,string? ApproverEmail,
        string? BossEmployeeId,string? BossName,string? BossEmail,
        string? LeaveApproverEmployeeId,string? LeaveApproverName,string? LeaveApproverEmail);
    private sealed record Recipient(string? EmployeeId,string Name,string? Email,string Type);
    private sealed record CommentRow(long Id,string AuthorEmployeeId,string AuthorName,string? AuthorEmail,
        string Text,DateTimeOffset CreatedAt);
}
