using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Hgvd.Core.Grab;

/// <summary>本地 CA + 按域名签发叶证书（MITM 用）。</summary>
/// <remarks>
/// ★★ 上次 C# 侧抓流被安卓拒绝，根因锁定在这两件事上，别再踩：
///
///   1. **证书必须"持久化"过一遍 PFX**。`X509Certificate2.CreateFromPemFile` /
///      `CopyWithPrivateKey` 在 Windows 上返回的是「临时私钥」，Schannel 拿去
///      握手会报没有私钥或直接失败。必须
///      `new X509Certificate2(cert.Export(X509ContentType.Pfx))` 转一次。
///   2. **必须把 CA 一起下发**。`SslStream.AuthenticateAsServer(cert)` 只发叶证书，
///      客户端拿不到签发者 → 链不完整 → 拒。要用
///      `SslStreamCertificateContext.Create(leaf, additional: [CA])`。
///
///   叶证书还必须有 SAN / EKU=serverAuth / CA:FALSE / KeyUsage / **AKI**。
///
/// ★ CA 与 Python 侧（`tools/metasec/mitm.py`）共用同一份文件，
///   因为模拟器里装的信任锚就是它；换一份等于要用户重装 CA。
/// </remarks>
public static class MitmCa
{
    /// <summary>CA 目录（ca.crt + ca.key）。</summary>
    public static string CaDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool", "ca");

    public static string CaCertPath => Path.Combine(CaDir, "ca.crt");
    public static string CaKeyPath => Path.Combine(CaDir, "ca.key");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, SslStreamCertificateContext> Contexts = new();
    private static X509Certificate2 _ca;
    private static X509Certificate2 _caPublic;   // ★ 无私钥副本，只用于构建证书链

    /// <summary>已加载的 CA（没有就生成一份）。</summary>
    public static X509Certificate2 Ca
    {
        get
        {
            lock (Gate)
            {
                if (_ca == null)
                {
                    _ca = LoadOrCreate();
                    // ★ Schannel 不接受 additionalCertificates 里带私钥的证书，
                    //   会报「处理证书时出现未知错误」（SEC_E_UNKNOWN_CREDENTIALS）。
                    //   Python 侧传的是纯公钥链，这里也要传无私钥副本。
                    _caPublic = new X509Certificate2(File.ReadAllBytes(CaCertPath));
                }
                return _ca;
            }
        }
    }

    /// <summary>确保 CA 存在（GUI 里"导出 CA 给用户安装"用这条）。</summary>
    public static X509Certificate2 Ensure() => Ca;

    private static X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(CaCertPath) && File.Exists(CaKeyPath))
            return Persist(X509Certificate2.CreateFromPemFile(CaCertPath, CaKeyPath));

        // 开发期兼容：项目里已有那份（模拟器上装的就是它）
        string devCrt = Path.Combine("_ref", "metasec", "ca", "ca.crt");
        string devKey = Path.Combine("_ref", "metasec", "ca", "ca.key");
        if (File.Exists(devCrt) && File.Exists(devKey))
        {
            try
            {
                Directory.CreateDirectory(CaDir);
                File.Copy(devCrt, CaCertPath, true);
                File.Copy(devKey, CaKeyPath, true);
                return Persist(X509Certificate2.CreateFromPemFile(CaCertPath, CaKeyPath));
            }
            catch (IOException) { /* 落到生成分支 */ }
        }

        return CreateNew();
    }

    /// <summary>★ 走一遍 PFX，私钥才真正可用（见类注释第 1 条）。</summary>
    private static X509Certificate2 Persist(X509Certificate2 cert) =>
        new(cert.Export(X509ContentType.Pfx));

    private static X509Certificate2 CreateNew()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=hgvd metasec lab CA, O=hgvd", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var made = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        Directory.CreateDirectory(CaDir);
        File.WriteAllText(CaCertPath, made.ExportCertificatePem());
        File.WriteAllText(CaKeyPath, rsa.ExportRSAPrivateKeyPem());
        return Persist(made);
    }

    /// <summary>取某域名的 TLS 服务端上下文（叶证书 + CA 链，带缓存）。</summary>
    /// <remarks>
    /// ★★ 关键：必须把 CA **拼进同一个 PEM 文件**再 load，跟 Python 侧
    ///   `chain = cert_pem + ca_pem; ctx.load_cert_chain(chain, key)` 完全一致。
    ///   `SslStreamCertificateContext.Create(leaf, additional: [CA])` 在 Schannel 上
    ///   不会把 additional 发给客户端 —— openssl 实测 depth=0，只有叶证书，
    ///   客户端因此无法构建到已安装的 CA 信任锚而拒绝。
    /// </remarks>
    public static SslStreamCertificateContext ContextFor(string host)
    {
        lock (Gate)
        {
            if (Contexts.TryGetValue(host, out var hit)) return hit;

            var (leafPem, keyPem) = IssueLeafPem(host);
            _ = Ca;   // 确保 CA 已加载
            string chainPem = leafPem.TrimEnd('\r', '\n') + "\n"
                              + File.ReadAllText(CaCertPath).TrimStart('\r', '\n');

            string chainFile = Path.Combine(Path.GetTempPath(),
                $"hgvd_chain_{Guid.NewGuid():N}.crt");
            string keyFile = Path.Combine(Path.GetTempPath(),
                $"hgvd_key_{Guid.NewGuid():N}.key");
            try
            {
                File.WriteAllText(chainFile, chainPem, new UTF8Encoding(false));
                File.WriteAllText(keyFile, keyPem, new UTF8Encoding(false));
                // ★ 整链 + 私钥一起加载，Schannel 才会把 CA 发给客户端
                var leaf = X509Certificate2.CreateFromPemFile(chainFile, keyFile);
                var ctx = SslStreamCertificateContext.Create(
                    Persist(leaf), null, offline: true);
                Contexts[host] = ctx;
                return ctx;
            }
            finally
            {
                try { File.Delete(chainFile); } catch { }
                try { File.Delete(keyFile); } catch { }
            }
        }
    }

    private static (string CertPem, string KeyPem) IssueLeafPem(string host)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));   // serverAuth
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
        else san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());

        // ★ AKI：缺它时部分安卓版本报"无法构建证书链"。
        //   ★★ 不能用 CreateFromCertificate —— 我们的 CA（Python 侧生成的）
        //      **没有 SKI 扩展**，那个重载会直接抛
        //      「does not have a Subject Key Identifier extension」。
        //      从 CA 公钥现算 SKI 即可（SKI 的标准定义就是公钥的 SHA-1），
        //      不需要 CA 证书里真的带这个扩展。
        var caKeyId = new X509SubjectKeyIdentifierExtension(Ca.PublicKey, false);
        req.CertificateExtensions.Add(
            X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(caKeyId));

        var serial = new byte[12];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;

        // ★ 有效期必须落在 CA 的有效期之内，否则 Create() 直接抛
        //   「requested notBefore is earlier than issuerCertificate.NotBefore」。
        //   CA 是 Python 侧早几天生成的，所以不能无脑 now-2d。
        var nowLocal = DateTimeOffset.Now;
        var caStart = new DateTimeOffset(Ca.NotBefore.ToUniversalTime());
        var caEnd = new DateTimeOffset(Ca.NotAfter.ToUniversalTime());
        var notBefore = nowLocal.AddDays(-2);
        if (notBefore < caStart) notBefore = caStart;
        var notAfter = nowLocal.AddYears(1);
        if (notAfter > caEnd) notAfter = caEnd;

        using var bare = req.Create(Ca, notBefore, notAfter, serial);
        return (bare.ExportCertificatePem(), key.ExportRSAPrivateKeyPem());
    }
}
