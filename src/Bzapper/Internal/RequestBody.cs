using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Bzapper.Internal;

/// <summary>Corpo de uma requisição; gera um <see cref="HttpContent"/> NOVO a cada tentativa.</summary>
internal abstract class RequestBody
{
    internal static RequestBody Json(BzapperObject value) => new JsonRequestBody(value.ToJsonString());

    internal static RequestBody Multipart(FileUpload file) => new MultipartRequestBody(file);

    internal abstract HttpContent CreateContent();

    private sealed class JsonRequestBody : RequestBody
    {
        private readonly byte[] _utf8;

        internal JsonRequestBody(string json) => _utf8 = Encoding.UTF8.GetBytes(json);

        internal override HttpContent CreateContent()
        {
            var content = new ByteArrayContent(_utf8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }
    }

    private sealed class MultipartRequestBody : RequestBody
    {
        private readonly FileUpload _file;

        internal MultipartRequestBody(FileUpload file) => _file = file;

        internal override HttpContent CreateContent()
        {
            var form = new MultipartFormDataContent();
            var part = new ByteArrayContent(_file.Content);
            part.Headers.ContentType = MediaTypeHeaderValue.Parse(_file.ContentType);
            form.Add(part, "file", _file.FileName);
            return form;
        }
    }
}
