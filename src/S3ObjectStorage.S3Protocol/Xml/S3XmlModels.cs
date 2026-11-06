using System.Xml.Serialization;

namespace S3ObjectStorage.S3Protocol.Xml;

/// <summary>
/// S3 的 XML 报文 DTO。字段顺序即 XML 元素顺序，请勿随意调整。
/// </summary>
[XmlRoot("ListAllMyBucketsResult", Namespace = S3Namespaces.S3)]
public sealed class ListAllMyBucketsResult
{
    public Owner Owner { get; set; } = new();

    [XmlArray("Buckets")]
    [XmlArrayItem("Bucket")]
    public List<BucketItem> Buckets { get; set; } = [];

    public sealed class BucketItem
    {
        public string Name { get; set; } = string.Empty;

        public string CreationDate { get; set; } = string.Empty;
    }
}

[XmlType(Namespace = S3Namespaces.S3)]
public sealed class Owner
{
    [XmlElement("ID")]
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}

[XmlRoot("ListBucketResult", Namespace = S3Namespaces.S3)]
public sealed class ListBucketResult
{
    public string Name { get; set; } = string.Empty;

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public int MaxKeys { get; set; }

    public int KeyCount { get; set; }

    public bool IsTruncated { get; set; }

    /// <summary>请求带 encoding-type=url 时回带 "url"，客户端据此解码 Key/Prefix。</summary>
    public string? EncodingType { get; set; }

    // ListObjectsV2 的分页字段
    public string? ContinuationToken { get; set; }

    public string? NextContinuationToken { get; set; }

    public string? StartAfter { get; set; }

    // ListObjectsV1 的分页字段
    public string? Marker { get; set; }

    public string? NextMarker { get; set; }

    [XmlElement("Contents")]
    public List<ObjectContents> Contents { get; set; } = [];

    [XmlElement("CommonPrefixes")]
    public List<CommonPrefix> CommonPrefixes { get; set; } = [];
}

[XmlType(Namespace = S3Namespaces.S3)]
public sealed class ObjectContents
{
    public string Key { get; set; } = string.Empty;

    public string LastModified { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;

    public long Size { get; set; }

    public string StorageClass { get; set; } = "STANDARD";

    [XmlElement("Owner")]
    public Owner? Owner { get; set; }
}

[XmlType(Namespace = S3Namespaces.S3)]
public sealed class CommonPrefix
{
    public string Prefix { get; set; } = string.Empty;
}

[XmlRoot("Error", Namespace = "")]
public sealed class ErrorResponse
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Resource { get; set; }

    public string? RequestId { get; set; }

    public string? ArgumentName { get; set; }

    public string? ArgumentValue { get; set; }
}

[XmlRoot("InitiateMultipartUploadResult", Namespace = S3Namespaces.S3)]
public sealed class InitiateMultipartUploadResult
{
    public string Bucket { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string UploadId { get; set; } = string.Empty;
}

[XmlRoot("CompleteMultipartUploadResult", Namespace = S3Namespaces.S3)]
public sealed class CompleteMultipartUploadResult
{
    public string Location { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;
}

[XmlRoot("ListPartsResult", Namespace = S3Namespaces.S3)]
public sealed class ListPartsResult
{
    public string Bucket { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string UploadId { get; set; } = string.Empty;

    public Owner? Initiator { get; set; }

    public Owner? Owner { get; set; }

    public string StorageClass { get; set; } = "STANDARD";

    public int PartNumberMarker { get; set; }

    public int NextPartNumberMarker { get; set; }

    public int MaxParts { get; set; }

    public bool IsTruncated { get; set; }

    [XmlElement("Part")]
    public List<PartItem> Parts { get; set; } = [];

    public sealed class PartItem
    {
        public int PartNumber { get; set; }

        public string LastModified { get; set; } = string.Empty;

        public string ETag { get; set; } = string.Empty;

        public long Size { get; set; }
    }
}

[XmlRoot("ListMultipartUploadsResult", Namespace = S3Namespaces.S3)]
public sealed class ListMultipartUploadsResult
{
    public string Bucket { get; set; } = string.Empty;

    public string? Prefix { get; set; }

    public string? Delimiter { get; set; }

    public int MaxUploads { get; set; }

    public bool IsTruncated { get; set; }

    /// <summary>请求带 encoding-type=url 时回带 "url"。</summary>
    public string? EncodingType { get; set; }

    public string? KeyMarker { get; set; }

    public string? UploadIdMarker { get; set; }

    public string? NextKeyMarker { get; set; }

    public string? NextUploadIdMarker { get; set; }

    [XmlElement("Upload")]
    public List<UploadItem> Uploads { get; set; } = [];

    public sealed class UploadItem
    {
        public string Key { get; set; } = string.Empty;

        public string UploadId { get; set; } = string.Empty;

        public Owner? Initiator { get; set; }

        public Owner? Owner { get; set; }

        public string StorageClass { get; set; } = "STANDARD";

        public string Initiated { get; set; } = string.Empty;
    }
}

[XmlRoot("DeleteResult", Namespace = S3Namespaces.S3)]
public sealed class DeleteResult
{
    [XmlElement("Deleted")]
    public List<DeletedObject> Deleted { get; set; } = [];

    [XmlElement("Error")]
    public List<DeleteError> Errors { get; set; } = [];

    public sealed class DeletedObject
    {
        public string Key { get; set; } = string.Empty;

        public string? VersionId { get; set; }
    }

    public sealed class DeleteError
    {
        public string Key { get; set; } = string.Empty;

        public string? VersionId { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }
}

[XmlRoot("Delete", Namespace = S3Namespaces.S3)]
[XmlType(Namespace = "")]
public sealed class DeleteObjectsRequest
{
    public bool Quiet { get; set; }

    [XmlElement("Object")]
    public List<ObjectIdentifier> Objects { get; set; } = [];

    [XmlType(Namespace = "")]
    public sealed class ObjectIdentifier
    {
        public string Key { get; set; } = string.Empty;

        public string? VersionId { get; set; }
    }
}

[XmlRoot("CompleteMultipartUpload", Namespace = S3Namespaces.S3)]
[XmlType(Namespace = "")]
public sealed class CompleteMultipartUploadRequest
{
    [XmlElement("Part")]
    public List<PartSpec> Parts { get; set; } = [];

    [XmlType(Namespace = "")]
    public sealed class PartSpec
    {
        public int PartNumber { get; set; }

        public string ETag { get; set; } = string.Empty;
    }
}

[XmlRoot("CreateBucketConfiguration", Namespace = S3Namespaces.S3)]
[XmlType(Namespace = "")]
public sealed class CreateBucketConfiguration
{
    public string? LocationConstraint { get; set; }
}

[XmlRoot("LocationConstraint", Namespace = S3Namespaces.S3)]
public sealed class LocationConstraint
{
    [XmlText]
    public string Value { get; set; } = string.Empty;
}

[XmlRoot("CopyObjectResult", Namespace = S3Namespaces.S3)]
public sealed class CopyObjectResult
{
    public string LastModified { get; set; } = string.Empty;

    public string ETag { get; set; } = string.Empty;
}

public static class S3Namespaces
{
    public const string S3 = "http://s3.amazonaws.com/doc/2006-03-01/";
}
