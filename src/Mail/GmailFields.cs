namespace Gmail.Console.Mail;

/// <summary>
/// Partial-response field masks. Gmail applies these server-side, so we get the MIME part
/// structure — filenames, sizes, attachment ids — without paying for the encoded body bytes.
/// That is what makes hydrating a 50-result search affordable (spec G07).
/// </summary>
public static class GmailFields
{
    private const string PartLeaf = "partId,mimeType,filename,headers,body/size,body/attachmentId";

    /// <summary>Structure only: headers plus three levels of MIME parts, no body data.</summary>
    public const string Structure =
        "id,threadId,labelIds,snippet,internalDate,sizeEstimate," +
        "payload(mimeType,filename,headers,body/size,body/attachmentId," +
        "parts(" + PartLeaf + ",parts(" + PartLeaf + ",parts(" + PartLeaf + "))))";

    /// <summary>The headers worth carrying into a search result.</summary>
    public static readonly string[] SummaryHeaders =
        ["From", "To", "Cc", "Subject", "Date", "Message-ID", "Reply-To"];

    public static string MetadataQuery() =>
        "format=metadata&" + string.Join('&', SummaryHeaders.Select(h => "metadataHeaders=" + Uri.EscapeDataString(h)));
}
