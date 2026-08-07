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

    // 锁定策略：按 (用户名+IP) 组合 5 次失败锁 5 分钟；另有全局限流兜底（防 XFF 伪造绕过）
    private const int MaxFailPerKey = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    // 全局限流：1 分钟窗口内全站最多 60 次登录失败，超限全局锁 60 秒（不依赖 IP 可信性）
    private const int GlobalFailMax = 60;
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);
    // 锁定字典容量上限（防内存膨胀）
    private const int MaxAttemptEntries = 10_000;

    private readonly Database _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int FailCount, DateTime? LockedUntil)> _attempts = new();
    private (int Count, DateTime WindowStart, DateTime? LockedUntil) _global = (0, DateTime.UtcNow, null);

    public AuthService(Database db) => _db = db;

    public bool IsInitialized => _db.HasAdmin();

    /// <summary>首次设置管理员（仅未初始化时允许）</summary>
    public string? Setup(string username, string password)
    {
        lock (_lock)
        {
            if (IsInitialized) return "系统已初始化，不能重复设置管理员";
            if (string.IsNullOrWhiteSpace(username) || username.Length < 3) return "用户名至少 3 个字符";
            if (string.IsNullOrEmpty(password) || password.Length < 8) return "密码至少 8 个字符";
            _db.CreateAdmin(username.Trim(), HashPassword(password));
            return null;
        }
    }

    /// <summary>登录校验；失败时计数锁定。返回 null=成功，否则为错误消息</summary>
    public string? Login(string username, string password, string ip)
    {
        lock (_lock)
        {
            TrimExpired();   // 惰性清理过期条目，防字典膨胀

            var key = $"{username}|{ip}";   // IP+用户名双键：伪造 XFF 无法锁死他人（需同用户名同 IP 组合）

            // 全局限流检查（不依赖 IP，防 XFF 伪造绕过锁定）
            var now = DateTime.UtcNow;
            if (now - _global.WindowStart > GlobalWindow)
                _global = (0, now, null);   // 窗口重置
            if (_global.LockedUntil is { } glUntil && glUntil > now)
                return $"尝试过于频繁，请稍后再试（全局限流）";
            if (_global.Count >= GlobalFailMax)
            {
                _global.LockedUntil = now.AddSeconds(60);
                return "尝试过于频繁，已临时限流 60 秒";
            }

            // 单键锁定检查
            if (_attempts.TryGetValue(key, out var a) && a.LockedUntil is { } until)
            {
                if (until > DateTime.UtcNow)
                    return $"尝试次数过多，已锁定至 {until.ToLocalTime():HH:mm:ss}，请稍后再试";
                _attempts.Remove(key);   // 锁定过期，清掉重来
            }

            var admin = _db.GetAdmin();
            if (admin == null) return "系统未初始化";
            if (admin.Value.Username != username || !VerifyPassword(password, admin.Value.PasswordHash))
            {
                var (cnt, _) = _attempts.TryGetValue(key, out var cur) ? cur : (0, null);
                cnt++;
                if (cnt >= MaxFailPerKey)
                {
                    _attempts[key] = (0, DateTime.UtcNow.Add(LockDuration));
                    _global.Count++;
                    return $"连续失败 {MaxFailPerKey} 次，账号锁定 5 分钟";
                }
                _attempts[key] = (cnt, null);
                _global.Count++;
                return $"用户名或密码错误（剩余 {MaxFailPerKey - cnt} 次机会）";
            }

            _attempts.Remove(key);
            return null;
        }
    }

    /// <summary>清除某键的失败计数（登录成功/登出时调用）</summary>
    public void ResetFailures(string username, string ip)
    {
        lock (_lock) _attempts.Remove($"{username}|{ip}");
    }

    /// <summary>修改密码：先验证旧密码（失败计入锁定），再更新哈希</summary>
    public string? ChangePassword(string currentUser, string oldPassword, string newPassword, string ip)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8) return "新密码至少 8 个字符";
        var loginErr = Login(currentUser, oldPassword, ip);
        if (loginErr != null) return loginErr;
        _db.CreateAdmin(currentUser, HashPassword(newPassword));
        ResetFailures(currentUser, ip);
        return null;
    }

    /// <summary>清理过期条目 + 容量上限（防字典无限膨胀）</summary>
    private void TrimExpired()
    {
        if (_attempts.Count > MaxAttemptEntries)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _attempts.Where(kv => kv.Value.LockedUntil is { } u && u <= now || kv.Value.FailCount == 0).Select(kv => kv.Key).ToList())
                _attempts.Remove(k);
            // 仍超限则清最旧一半（简单粗暴但有效）
            if (_attempts.Count > MaxAttemptEntries)
                _attempts.Clear();
        }
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
