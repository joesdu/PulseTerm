using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>
/// 桶配置的 JSON ↔ 结构化表单双向映射。
/// <para>
/// **关键取舍:表单写回时走 JSON DOM 增量修改,而不是从字段重新构造整份文档。**
/// 后者会把表单不认识的字段(服务端新加的、或该实现特有的)在保存时静默清空 ——
/// 那是会真的改坏用户配置的。这里读进来的 <see cref="JsonNode" /> 原样保留,
/// 只覆盖表单确实编辑过的那几个路径。
/// </para>
/// <para>
/// 只有字段少、形状稳定的配置才有表单;规则数组、策略这类**本身就是文档**的配置
/// 走 JSON 编辑器(见 <see cref="S3ConfigEditor" />)。
/// </para>
/// </summary>
internal static class S3ConfigForm
{
    /// <summary>按配置项构造表单字段,并用 <paramref name="json" /> 里的当前值填好。</summary>
    /// <param name="kind">配置项。</param>
    /// <param name="json">当前配置文档。</param>
    /// <param name="loc">插件文案表。</param>
    /// <returns>表单字段。</returns>
    public static List<S3FormFieldViewModel> Build(S3ConfigKind kind, string json, Loc loc)
    {
        JsonNode? root = Parse(json);
        List<S3FormFieldViewModel> fields = Describe(kind, loc);
        foreach (S3FormFieldViewModel field in fields)
        {
            Fill(field, root);
        }
        return fields;
    }

    /// <summary>把表单里的值写回 JSON 文档;文档里其余内容原样保留。</summary>
    public static string Apply(S3ConfigKind kind, string json, IReadOnlyList<S3FormFieldViewModel> fields)
    {
        JsonNode root = Parse(json) ?? new JsonObject();
        if (root is not JsonObject)
        {
            root = new JsonObject();
        }
        foreach (S3FormFieldViewModel field in fields)
        {
            Write(root, field);
        }
        // 有些配置的载荷是"必须存在的空壳"(例如版本控制只有一个 Status),
        // 这里保证根始终是对象,服务端才不会收到一个 null。
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>该配置是否提供结构化表单。</summary>
    public static bool HasForm(S3ConfigKind kind) => S3ConfigDescriptor.For(kind).Editor == S3ConfigEditor.Form;

    // ---- 各配置的字段声明 ---------------------------------------------------

    private static List<S3FormFieldViewModel> Describe(S3ConfigKind kind, Loc loc) =>
        kind switch
        {
            S3ConfigKind.Versioning =>
            [
                Choice(loc, "Status", "S3Fld_VersioningStatus", ["Enabled", "Suspended", "Off"], "S3Fld_VersioningHint"),
                Toggle(loc, "EnableMfaDelete", "S3Fld_MfaDelete", "S3Fld_MfaDeleteHint"),
            ],
            S3ConfigKind.PublicAccessBlock =>
            [
                Toggle(loc, "BlockPublicAcls", "S3Fld_BlockPublicAcls"),
                Toggle(loc, "IgnorePublicAcls", "S3Fld_IgnorePublicAcls"),
                Toggle(loc, "BlockPublicPolicy", "S3Fld_BlockPublicPolicy"),
                Toggle(loc, "RestrictPublicBuckets", "S3Fld_RestrictPublicBuckets"),
            ],
            S3ConfigKind.OwnershipControls =>
            [
                Choice(loc, "Rules[0].ObjectOwnership", "S3Fld_ObjectOwnership",
                    ["BucketOwnerEnforced", "BucketOwnerPreferred", "ObjectWriter"], "S3Fld_ObjectOwnershipHint"),
            ],
            S3ConfigKind.Encryption =>
            [
                Choice(loc, "ServerSideEncryptionRules[0].ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm",
                    "S3Fld_EncryptionAlgorithm", ["AES256", "aws:kms", "aws:kms:dsse"], "S3Fld_EncryptionAlgorithmHint"),
                Text(loc, "ServerSideEncryptionRules[0].ServerSideEncryptionByDefault.ServerSideEncryptionKeyManagementServiceKeyId",
                    "S3Fld_KmsKeyId", "S3Fld_KmsKeyIdHint"),
                Toggle(loc, "ServerSideEncryptionRules[0].BucketKeyEnabled", "S3Fld_BucketKey", "S3Fld_BucketKeyHint"),
            ],
            S3ConfigKind.ObjectLock =>
            [
                Choice(loc, "ObjectLockEnabled", "S3Fld_ObjectLockEnabled", ["Enabled"], "S3Fld_ObjectLockEnabledHint"),
                Choice(loc, "Rule.DefaultRetention.Mode", "S3Fld_RetentionMode", ["GOVERNANCE", "COMPLIANCE"], "S3Fld_RetentionModeHint"),
                Number(loc, "Rule.DefaultRetention.Days", "S3Fld_RetentionDays"),
                Number(loc, "Rule.DefaultRetention.Years", "S3Fld_RetentionYears"),
            ],
            S3ConfigKind.Tagging => [TagList(loc, "TagSet", "S3Fld_Tags", "S3Fld_TagsHint")],
            S3ConfigKind.Website =>
            [
                Text(loc, "IndexDocumentSuffix", "S3Fld_IndexDocument", "S3Fld_IndexDocumentHint"),
                Text(loc, "ErrorDocument", "S3Fld_ErrorDocument"),
            ],
            S3ConfigKind.Logging =>
            [
                Text(loc, "TargetBucketName", "S3Fld_LogTargetBucket", "S3Fld_LogTargetBucketHint"),
                Text(loc, "TargetPrefix", "S3Fld_LogTargetPrefix"),
            ],
            S3ConfigKind.AccelerateConfiguration =>
            [
                Choice(loc, "Status", "S3Fld_AccelerateStatus", ["Enabled", "Suspended"], "S3Fld_AccelerateHint"),
            ],
            S3ConfigKind.RequestPayment =>
            [
                Choice(loc, "Payer", "S3Fld_Payer", ["BucketOwner", "Requester"], "S3Fld_PayerHint"),
            ],
            _ => [],
        };

    // ---- 字段构造小工具 -----------------------------------------------------

    private static S3FormFieldViewModel Text(Loc loc, string path, string labelKey, string hintKey = "") =>
        new() { Path = path, Label = loc.Get(labelKey), Kind = S3FieldKind.Text, Hint = Hint(loc, hintKey) };

    private static S3FormFieldViewModel Number(Loc loc, string path, string labelKey, string hintKey = "") =>
        new() { Path = path, Label = loc.Get(labelKey), Kind = S3FieldKind.Number, Hint = Hint(loc, hintKey) };

    private static S3FormFieldViewModel Toggle(Loc loc, string path, string labelKey, string hintKey = "") =>
        new() { Path = path, Label = loc.Get(labelKey), Kind = S3FieldKind.Toggle, Hint = Hint(loc, hintKey) };

    private static S3FormFieldViewModel TagList(Loc loc, string path, string labelKey, string hintKey = "") =>
        new() { Path = path, Label = loc.Get(labelKey), Kind = S3FieldKind.TagList, Hint = Hint(loc, hintKey) };

    private static S3FormFieldViewModel Choice(Loc loc, string path, string labelKey, string[] choices, string hintKey = "")
    {
        var field = new S3FormFieldViewModel
        {
            Path = path,
            Label = loc.Get(labelKey),
            Kind = S3FieldKind.Choice,
            Hint = Hint(loc, hintKey),
        };
        // 允许留空:很多配置项"不设置"与"设成某个值"是两回事。
        field.Choices.Add(string.Empty);
        foreach (string choice in choices)
        {
            field.Choices.Add(choice);
        }
        return field;
    }

    private static string Hint(Loc loc, string key) => key.Length == 0 ? string.Empty : loc.Get(key);

    // ---- JSON DOM 读写 ------------------------------------------------------

    private static JsonNode? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Fill(S3FormFieldViewModel field, JsonNode? root)
    {
        JsonNode? node = Resolve(root, field.Path, create: false);
        if (field.Kind == S3FieldKind.TagList)
        {
            field.Tags.Clear();
            foreach (JsonNode? item in node as JsonArray ?? [])
            {
                field.Tags.Add(new()
                {
                    Key = item?["Key"]?.GetValue<string>() ?? string.Empty,
                    Value = item?["Value"]?.GetValue<string>() ?? string.Empty,
                });
            }
            return;
        }
        if (node is null)
        {
            field.Text = string.Empty;
            field.Toggle = false;
            return;
        }
        if (field.Kind == S3FieldKind.Toggle)
        {
            field.Toggle = node.GetValueKind() == JsonValueKind.True;
            return;
        }
        field.Text = node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static void Write(JsonNode root, S3FormFieldViewModel field)
    {
        if (field.Kind == S3FieldKind.TagList)
        {
            var array = new JsonArray();
            foreach (S3TagRowViewModel row in field.Tags)
            {
                // 只有值没有键的行不是标签,是用户还没填完的空行 —— 丢掉。
                // 要按 Trim 判断:全是空格的键会被服务端拒掉,却能通过长度检查。
                string key = row.Key.Trim();
                if (key.Length > 0)
                {
                    array.Add(new JsonObject { ["Key"] = key, ["Value"] = row.Value });
                }
            }
            Assign(root, field.Path, array);
            return;
        }
        if (field.Kind == S3FieldKind.Toggle)
        {
            Assign(root, field.Path, JsonValue.Create(field.Toggle));
            return;
        }
        string text = field.Text.Trim();
        if (text.Length == 0)
        {
            // 留空 = 不设置。写 null 让序列化时被忽略,而不是发一个空串上去
            // (空串在不少配置里是**非法值**,会被服务端拒掉)。
            Assign(root, field.Path, null);
            return;
        }
        JsonNode? value = field.Kind == S3FieldKind.Number && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
            ? JsonValue.Create(number)
            : JsonValue.Create(text);
        Assign(root, field.Path, value);
    }

    /// <summary>
    /// 按点号路径定位节点,支持 <c>a.b[0].c</c> 的数组下标。
    /// <paramref name="create" /> 为真时沿途补齐缺失的对象/数组。
    /// </summary>
    private static JsonNode? Resolve(JsonNode? root, string path, bool create)
    {
        JsonNode? current = root;
        foreach (string rawSegment in path.Split('.'))
        {
            if (current is null)
            {
                return null;
            }
            string segment = rawSegment;
            int bracket = segment.IndexOf('[');
            int index = -1;
            if (bracket >= 0 && segment.EndsWith(']'))
            {
                _ = int.TryParse(segment[(bracket + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
                segment = segment[..bracket];
            }

            if (current is not JsonObject obj)
            {
                return null;
            }
            JsonNode? next = obj[segment];
            if (next is null)
            {
                if (!create)
                {
                    return null;
                }
                next = index >= 0 ? new JsonArray() : new JsonObject();
                obj[segment] = next;
            }
            if (index >= 0)
            {
                if (next is not JsonArray array)
                {
                    return null;
                }
                while (create && array.Count <= index)
                {
                    array.Add(new JsonObject());
                }
                if (array.Count <= index)
                {
                    return null;
                }
                next = array[index];
            }
            current = next;
        }
        return current;
    }

    /// <summary>把值写到路径上,沿途补齐容器。</summary>
    private static void Assign(JsonNode root, string path, JsonNode? value)
    {
        int lastDot = path.LastIndexOf('.');
        string parentPath = lastDot < 0 ? string.Empty : path[..lastDot];
        string leaf = lastDot < 0 ? path : path[(lastDot + 1)..];

        JsonNode? parent = parentPath.Length == 0 ? root : Resolve(root, parentPath, create: true);
        int bracket = leaf.IndexOf('[');
        if (bracket >= 0 && leaf.EndsWith(']'))
        {
            // 叶子本身带下标(如 TagSet[0]):先拿到数组再按位置写。
            string name = leaf[..bracket];
            if (!int.TryParse(leaf[(bracket + 1)..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                parent is not JsonObject holder)
            {
                return;
            }
            if (holder[name] is not JsonArray array)
            {
                array = [];
                holder[name] = array;
            }
            while (array.Count <= index)
            {
                array.Add(new JsonObject());
            }
            array[index] = value;
            return;
        }
        if (parent is JsonObject target)
        {
            target[leaf] = value;
        }
    }
}
