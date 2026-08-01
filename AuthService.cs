using System.Security.Cryptography;

namespace EmqxMonitor;

/// <summary>
/// 管理员认证：PBKDF2-SHA256 哈希 + 登录失败锁定。
/// 存储格式: "iterations.salt_b64.hash_b64"
/// </summary>
public class AuthService
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly Database _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int FailCount, DateTime? LockedUntil)> _attempts = new();

    public AuthService(Database db) => _db = db;

    public bool IsInitialized => _db.HasAdmin();

    /// <summary>首次设置管理员（仅未初始化时允许）</summary>
    public string? Setup(string username, string password)
    {
        if (IsInitialized) return "系统已初始化，不能重复设置管理员";
        if (string.IsNullOrWhiteSpace(username) || username.Length < 3) return "用户名至少 3 个字符";
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "密码至少 8 个字符";
        _db.CreateAdmin(username.Trim(), HashPassword(password));
        return null;
    }

    /// <summary>登录校验；失败时计数锁定。返回 null=成功，否则为错误消息</summary>
    public string? Login(string username, string password, string ip)
    {
        lock (_lock)
        {
            // 锁定检查
            if (_attempts.TryGetValue(ip, out var a) && a.LockedUntil is { } until)
            {
                if (until > DateTime.UtcNow)
                    return $"尝试次数过多，已锁定至 {until.ToLocalTime():HH:mm:ss}，请稍后再试";
                _attempts.Remove(ip);   // 锁定过期，清掉重来
            }

            var admin = _db.GetAdmin();
            if (admin == null) return "系统未初始化";
            if (admin.Value.Username != username || !VerifyPassword(password, admin.Value.PasswordHash))
            {
                var (cnt, _) = _attempts.TryGetValue(ip, out var cur) ? cur : (0, null);
                cnt++;
                if (cnt >= 5)
                {
                    _attempts[ip] = (0, DateTime.UtcNow.AddMinutes(5));
                    return "连续失败 5 次，账号锁定 5 分钟";
                }
                _attempts[ip] = (cnt, null);
                return $"用户名或密码错误（剩余 {5 - cnt} 次机会）";
            }

            _attempts.Remove(ip);
            return null;
        }
    }

    /// <summary>清除某 IP 的失败计数（登录成功/登出时调用）</summary>
    public void ResetFailures(string ip)
    {
        lock (_lock) _attempts.Remove(ip);
    }

    /// <summary>修改密码：先验证旧密码（失败计入锁定），再更新哈希</summary>
    public string? ChangePassword(string currentUser, string oldPassword, string newPassword, string ip)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8) return "新密码至少 8 个字符";
        var loginErr = Login(currentUser, oldPassword, ip);
        if (loginErr != null) return loginErr;
        _db.CreateAdmin(currentUser, HashPassword(newPassword));
        ResetFailures(ip);
        return null;
    }

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;
            var iterations = int.Parse(parts[0]);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
