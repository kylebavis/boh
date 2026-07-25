namespace Boh.Web.Data.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
