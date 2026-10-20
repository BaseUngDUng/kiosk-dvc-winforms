<%@ WebHandler Language="C#" Class="KioskUpdateUploader" %>

using System;
using System.IO;
using System.Web;

public class KioskUpdateUploader : IHttpHandler
{
    // Mat khau bao mat khi day ban cap nhat tu may lap trinh len Server
    private const string SECRET_KEY = "VNPT@!56";

    public void ProcessRequest(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        if (context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 405;
            context.Response.Write("{\"ok\":false,\"message\":\"Method Not Allowed. Use POST.\"}");
            return;
        }

        string apiKey = context.Request.Headers["X-Update-Key"];
        if (string.IsNullOrEmpty(apiKey) || apiKey != SECRET_KEY)
        {
            context.Response.StatusCode = 401;
            context.Response.Write("{\"ok\":false,\"message\":\"Unauthorized: Sai X-Update-Key\"}");
            return;
        }

        try
        {
            string serverDir = context.Server.MapPath("~/");
            string fileName = context.Request.Headers["X-File-Name"];

            if (string.IsNullOrEmpty(fileName) ||
                (fileName != "KIOSK_Update.zip" && fileName != "version.json"))
            {
                context.Response.StatusCode = 400;
                context.Response.Write("{\"ok\":false,\"message\":\"Invalid X-File-Name. Only KIOSK_Update.zip or version.json allowed.\"}");
                return;
            }

            string targetPath = Path.Combine(serverDir, fileName);
            using (FileStream fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                context.Request.InputStream.CopyTo(fs);
            }

            FileInfo fi = new FileInfo(targetPath);
            context.Response.StatusCode = 200;
            context.Response.Write(string.Format("{{\"ok\":true,\"file\":\"{0}\",\"size\":{1},\"updatedAt\":\"{2:yyyy-MM-dd HH:mm:ss}\"}}",
                fileName, fi.Length, DateTime.Now));
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            string safeMsg = ex.Message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            context.Response.Write("{\"ok\":false,\"message\":\"" + safeMsg + "\"}");
        }
    }

    public bool IsReusable
    {
        get { return false; }
    }
}
