namespace S3ObjectStorage.Domain.Exceptions;

/// <summary>
/// S3 错误码与其对应的 HTTP 状态码。
/// </summary>
public sealed record S3ErrorCode(int StatusCode, string Code, string Message)
{
    // 通用
    public static readonly S3ErrorCode AccessDenied = new(403, "AccessDenied", "Access Denied");
    public static readonly S3ErrorCode InternalError = new(500, "InternalError", "We encountered an internal error. Please try again.");
    public static readonly S3ErrorCode NotImplemented = new(501, "NotImplemented", "A header or query you provided is not supported at this time.");
    public static readonly S3ErrorCode MethodNotAllowed = new(405, "MethodNotAllowed", "The specified method is not allowed against this resource.");
    public static readonly S3ErrorCode InvalidRequest = new(400, "InvalidRequest", "An invalid request was received.");
    public static readonly S3ErrorCode BadDigest = new(400, "BadDigest", "The Content-MD5 you specified did not match what we received.");
    public static readonly S3ErrorCode RequestTimeTooSkewed = new(403, "RequestTimeTooSkewed", "The difference between the request time and the current time is too large.");
    public static readonly S3ErrorCode MissingContentLength = new(411, "MissingContentLength", "You must provide the Content-Length HTTP header.");
    public static readonly S3ErrorCode InvalidArgument = new(400, "InvalidArgument", "Invalid Argument");

    // 桶
    public static readonly S3ErrorCode NoSuchBucket = new(404, "NoSuchBucket", "The specified bucket does not exist");
    public static readonly S3ErrorCode BucketAlreadyExists = new(409, "BucketAlreadyExists", "The requested bucket name is not available. Please select a different name and try again.");
    public static readonly S3ErrorCode BucketAlreadyOwnedByYou = new(409, "BucketAlreadyOwnedByYou", "Your previous request to create the named bucket succeeded and you already own it.");
    public static readonly S3ErrorCode BucketNotEmpty = new(409, "BucketNotEmpty", "The bucket you tried to delete is not empty");
    public static readonly S3ErrorCode InvalidBucketName = new(400, "InvalidBucketName", "The specified bucket is not valid.");
    public static readonly S3ErrorCode InvalidLocationConstraint = new(400, "InvalidLocationConstraint", "The specified location-constraint is not valid");

    // 对象
    public static readonly S3ErrorCode NoSuchKey = new(404, "NoSuchKey", "The specified key does not exist.");
    public static readonly S3ErrorCode KeyTooLongError = new(400, "KeyTooLongError", "Your key is too long.");
    public static readonly S3ErrorCode InvalidRange = new(416, "InvalidRange", "The requested range is not satisfiable");

    // 分片上传
    public static readonly S3ErrorCode NoSuchUpload = new(404, "NoSuchUpload", "The specified multipart upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.");
    public static readonly S3ErrorCode InvalidPart = new(400, "InvalidPart", "One or more of the specified parts could not be found. The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.");
    public static readonly S3ErrorCode InvalidPartOrder = new(400, "InvalidPartOrder", "The list of parts was not in ascending order. Parts list must be specified in order by part number.");
    public static readonly S3ErrorCode InvalidPartNumber = new(400, "InvalidArgument", "Part number must be an integer between 1 and 10000, inclusive.");
    public static readonly S3ErrorCode EntityTooSmall = new(400, "EntityTooSmall", "Your proposed upload is smaller than the minimum allowed object size.");
    public static readonly S3ErrorCode EntityTooLarge = new(400, "EntityTooLarge", "Your proposed upload exceeds the maximum allowed object size.");
    public static readonly S3ErrorCode MetadataTooLarge = new(400, "MetadataTooLarge", "Your metadata headers exceed the maximum allowed metadata size.");

    public static readonly S3ErrorCode MalformedXML = new(400, "MalformedXML", "The XML you provided was not well-formed or did not validate against our published schema.");

    // 认证
    public static readonly S3ErrorCode SignatureDoesNotMatch = new(403, "SignatureDoesNotMatch", "The request signature we calculated does not match the signature you provided. Check your key and signing method.");
    public static readonly S3ErrorCode MissingSecurityHeader = new(400, "MissingSecurityHeader", "Your request was missing a required header.");
    public static readonly S3ErrorCode InvalidAccessKeyId = new(403, "InvalidAccessKeyId", "The AWS access key ID that you provided does not exist in our records.");
    public static readonly S3ErrorCode XAmzContentSHA256Mismatch = new(400, "XAmzContentSHA256Mismatch", "The provided 'x-amz-content-sha256' header does not match what was computed.");
    public static readonly S3ErrorCode AuthorizationHeaderMalformed = new(400, "AuthorizationHeaderMalformed", "The authorization header is malformed.");
    public static readonly S3ErrorCode MissingSignature = new(403, "AccessDenied", "Anonymous access is not allowed.");
    public static readonly S3ErrorCode TemporaryRedirect = new(307, "TemporaryRedirect", "Please re-send this request to the specified temporary endpoint.");
}
