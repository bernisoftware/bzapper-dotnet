using System;
using System.Collections.Generic;
using System.IO;

namespace Bzapper;

/// <summary>
/// Arquivo para as operações de upload (<c>multipart/form-data</c>): logo da marca/projeto e mídia de campanha.
/// </summary>
/// <example>
/// <code>
/// var logo = FileUpload.FromFile("logo.png");                         // do disco
/// var same = FileUpload.FromBytes(bytes, "logo.png", "image/png");    // da memória
/// await bz.UploadBrandLogoAsync(logo);
/// </code>
/// </example>
public sealed class FileUpload
{
    private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".mp4"] = "video/mp4",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/ogg",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
    };

    private FileUpload(byte[] content, string fileName, string contentType)
    {
        Content = content;
        FileName = fileName;
        ContentType = contentType;
    }

    /// <summary>Bytes do arquivo.</summary>
    public byte[] Content { get; }

    /// <summary>Nome do arquivo (vai no <c>filename</c> do multipart).</summary>
    public string FileName { get; }

    /// <summary>Tipo MIME (ex.: <c>image/png</c>).</summary>
    public string ContentType { get; }

    /// <summary>Arquivo a partir de bytes em memória.</summary>
    /// <param name="content">Bytes do arquivo.</param>
    /// <param name="fileName">Nome do arquivo (ex.: <c>logo.png</c>).</param>
    /// <param name="contentType">Tipo MIME; sem ele, deduzido da extensão (ou <c>application/octet-stream</c>).</param>
    /// <returns>O arquivo.</returns>
    public static FileUpload FromBytes(byte[] content, string fileName, string? contentType = null)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Informe o nome do arquivo.", nameof(fileName));
        }

        return new FileUpload(content, fileName, string.IsNullOrWhiteSpace(contentType) ? Guess(fileName) : contentType!);
    }

    /// <summary>Arquivo a partir de um stream (lido inteiro agora).</summary>
    /// <param name="stream">Conteúdo.</param>
    /// <param name="fileName">Nome do arquivo.</param>
    /// <param name="contentType">Tipo MIME; sem ele, deduzido da extensão.</param>
    /// <returns>O arquivo.</returns>
    public static FileUpload FromStream(Stream stream, string fileName, string? contentType = null)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return FromBytes(buffer.ToArray(), fileName, contentType);
    }

    /// <summary>Arquivo a partir de um caminho no disco (lido inteiro agora).</summary>
    /// <param name="path">Caminho do arquivo.</param>
    /// <param name="contentType">Tipo MIME; sem ele, deduzido da extensão.</param>
    /// <returns>O arquivo.</returns>
    public static FileUpload FromFile(string path, string? contentType = null) =>
        FromBytes(File.ReadAllBytes(path), Path.GetFileName(path), contentType);

    private static string Guess(string fileName) =>
        ContentTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";
}
