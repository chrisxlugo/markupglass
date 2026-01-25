using System;
using System.IO;
using System.Text.Json;
using MarkupGlass.Models;

namespace MarkupGlass.Services;

internal sealed class SessionStore
{
    private readonly string _sessionPath;

    public SessionStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarkupGlass");
        Directory.CreateDirectory(baseDir);
        _sessionPath = Path.Combine(baseDir, "last-session.json");
    }

    public void Save(AnnotationSession session)
    {
        var json = Serialize(session);
        File.WriteAllText(_sessionPath, json);
    }

    public AnnotationSession? Load()
    {
        if (!File.Exists(_sessionPath))
        {
            return null;
        }

        var json = File.ReadAllText(_sessionPath);
        return Deserialize(json);
    }

    public string Serialize(AnnotationSession session)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        return JsonSerializer.Serialize(session, options);
    }

    public AnnotationSession? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<AnnotationSession>(json);
    }
}
