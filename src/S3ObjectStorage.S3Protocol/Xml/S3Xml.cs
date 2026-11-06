using System.Collections.Concurrent;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using S3ObjectStorage.Domain.Exceptions;

namespace S3ObjectStorage.S3Protocol.Xml;

/// <summary>
/// 统一负责 S3 XML 报文的输入输出。
/// </summary>
public static class S3Xml
{
    public const string ContentType = "application/xml";

    /// <summary>XML 请求体的默认上限：S3 的 XML 报文（批量删除 / 完成分片）都很小，2 MiB 足够。</summary>
    public const int DefaultMaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>
    /// XML 允许的最大嵌套深度。S3 的 XML 报文层级都很浅（&lt; 10），
    /// 深度限制用于拦截「极深嵌套」的畸形文档 —— 这类文档体积很小，但会让解析器
    /// 长时间占用 CPU，属于低成本 DoS 面。
    /// </summary>
    public const int MaxNestingDepth = 64;

    private static readonly ConcurrentDictionary<(Type Type, string Namespace), XmlSerializer> SerializerCache = new();

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = DefaultMaxBodyBytes,
    };

    /// <summary>
    /// 扫描正文，遇到超过 <paramref name="maxDepth"/> 的嵌套立即抛 <see cref="S3ErrorCode.MalformedXML"/>。
    /// 只做标签层级计数：跳过注释/声明/处理指令，自闭合标签不计层。
    /// </summary>
    private static void GuardNestingDepth(MemoryStream buffer, int maxDepth)
    {
        if (!buffer.TryGetBuffer(out var segment))
        {
            return;
        }

        var data = segment.AsSpan();
        var depth = 0;
        var i = 0;

        while (i < data.Length)
        {
            if (data[i] != (byte)'<')
            {
                i++;
                continue;
            }

            if (i + 1 >= data.Length)
            {
                break;
            }

            var next = data[i + 1];
            if (next == (byte)'/' || next == (byte)'!' || next == (byte)'?')
            {
                // 结束标签、注释、CDATA、声明：不计入深度（结束标签的减法在下方统一处理）
                if (next == (byte)'/')
                {
                    depth--;
                }

                i = SkipToTagEnd(data, i);
                continue;
            }

            var end = SkipToTagEnd(data, i);
            var selfClosing = end - 1 > i && data[end - 1] == (byte)'/';
            if (!selfClosing)
            {
                depth++;
                if (depth > maxDepth)
                {
                    throw new S3Exception(S3ErrorCode.MalformedXML, $"XML 嵌套超过 {maxDepth} 层");
                }
            }

            i = end;
        }
    }

    /// <summary>返回标签结束符 <c>&gt;</c> 之后的位置；跳过属性中的引号内容。</summary>
    private static int SkipToTagEnd(ReadOnlySpan<byte> data, int start)
    {
        var quote = (byte)0;
        for (var i = start + 1; i < data.Length; i++)
        {
            var c = data[i];
            if (quote != 0)
            {
                if (c == quote)
                {
                    quote = 0;
                }

                continue;
            }

            if (c == (byte)'"' || c == (byte)'\'')
            {
                quote = c;
            }
            else if (c == (byte)'>')
            {
                return i + 1;
            }
        }

        return data.Length;
    }

    /// <summary>把 DTO 序列化成带 XML 声明的正文。</summary>
    public static string Serialize<T>(T value, string? defaultNamespace = null, bool omitDeclaration = false)
    {
        var serializer = GetSerializer(typeof(T), defaultNamespace ?? string.Empty);
        var namespaces = new XmlSerializerNamespaces();
        if (!string.IsNullOrEmpty(defaultNamespace))
        {
            namespaces.Add(string.Empty, defaultNamespace);
        }

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = omitDeclaration,
            NamespaceHandling = NamespaceHandling.OmitDuplicates,
        };

        var writer = new EncodedStringWriter();
        using var xmlWriter = XmlWriter.Create(writer, settings);
        serializer.Serialize(xmlWriter, value, namespaces);
        xmlWriter.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// 反序列化请求正文。ASP.NET Core 默认禁用同步 IO，
    /// 而 XmlSerializer 是同步读的，因此先异步缓冲到内存（带上限，避免内存被撑爆）。
    /// </summary>
    public static async Task<T> DeserializeAsync<T>(
        Stream stream,
        int maxBytes = DefaultMaxBodyBytes,
        CancellationToken cancellationToken = default) where T : class
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new S3Exception(
                    S3ErrorCode.EntityTooLarge,
                    $"XML 请求体不得超过 {maxBytes} 字节。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        GuardNestingDepth(buffer, MaxNestingDepth);
        buffer.Position = 0;
        return Deserialize<T>(buffer);
    }

    /// <summary>
    /// 反序列化请求正文。
    /// 注意：客户端有的带 <c>xmlns</c>、有的不带（aws cli 与各 SDK 行为不一致），
    /// 这里统一剥离命名空间后再交给「无命名空间」的序列化器，两种形态都能解析。
    /// </summary>
    public static T Deserialize<T>(Stream stream) where T : class
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        var document = XDocument.Load(reader);
        document.RemoveNamespaceDeclarations();

        using var cleaned = new MemoryStream();
        document.Save(cleaned, SaveOptions.DisableFormatting);
        cleaned.Position = 0;

        using var cleanReader = XmlReader.Create(cleaned, ReaderSettings);
        var serializer = CreateUnqualifiedSerializer<T>();
        return (T)serializer.Deserialize(cleanReader)!;
    }

    private static XmlSerializer GetSerializer(Type type, string ns)
        => SerializerCache.GetOrAdd((type, ns), static key => new XmlSerializer(key.Type, key.Namespace));

    private static XmlSerializer CreateUnqualifiedSerializer<T>()
    {
        return SerializerCache.GetOrAdd((typeof(T), "\u0000unqualified"), static key =>
        {
            var root = Attribute.GetCustomAttribute(key.Type, typeof(XmlRootAttribute)) as XmlRootAttribute;
            return root is null
                ? new XmlSerializer(key.Type)
                // defaultNamespace 传空串：根节点与子节点都按「无命名空间」解析。
                : new XmlSerializer(key.Type, null, [], new XmlRootAttribute(root.ElementName), string.Empty);
        });
    }

    /// <summary>S3 约定的时间戳格式：2026-10-29T01:02:03.000Z。</summary>
    public static string ToS3Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class EncodedStringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    private static void RemoveNamespaceDeclarations(this XDocument document)
    {
        foreach (var element in document.Descendants())
        {
            if (element.Name.Namespace != XNamespace.None)
            {
                element.Name = XName.Get(element.Name.LocalName);
            }

            foreach (var attribute in element.Attributes().ToList())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    attribute.Remove();
                    continue;
                }

                if (attribute.Name.Namespace != XNamespace.None)
                {
                    element.SetAttributeValue(attribute.Name.LocalName, attribute.Value);
                }
            }
        }
    }
}
