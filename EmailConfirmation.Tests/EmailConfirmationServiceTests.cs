using EmailConfirmation.Data;
using EmailConfirmation.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace EmailConfirmation.Tests;

/// <summary>
/// Unit Tests kiểm tra toàn bộ Acceptance Criteria của chức năng xác nhận email.
/// </summary>
public class EmailConfirmationServiceTests : IAsyncLifetime
{
    private IServiceProvider _serviceProvider = null!;
    private UserManager<ApplicationUser> _userManager = null!;
    private Mock<IEmailService> _emailServiceMock = null!;
    private EmailConfirmationService _sut = null!;

    private ApplicationUser _testUser = null!;

    // ── Setup & Teardown ──────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddLogging();

        // Đăng ký DbContext với InMemory database riêng cho mỗi test
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseInMemoryDatabase(dbName));

        // Đăng ký Identity đầy đủ (bao gồm token providers)
        services
            .AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.SignIn.RequireConfirmedEmail = true;
                options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
                options.Password.RequireDigit = false;
                options.Password.RequiredLength = 6;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();  // ← Đăng ký EmailTokenProvider


        _serviceProvider = services.BuildServiceProvider();
        _userManager = _serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        _emailServiceMock = new Mock<IEmailService>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:BaseUrl"] = "https://localhost:7001"
            })
            .Build();

        _sut = new EmailConfirmationService(
            _userManager,
            _emailServiceMock.Object,
            config,
            NullLogger<EmailConfirmationService>.Instance);

        // Tạo user test chưa xác nhận email
        _testUser = new ApplicationUser
        {
            UserName = "buyer@example.com",
            Email = "buyer@example.com",
            FullName = "Người Mua Hàng",
            EmailConfirmed = false
        };
        var result = await _userManager.CreateAsync(_testUser, "P@ssw0rd!");
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Không tạo được user test: {string.Join(", ", result.Errors.Select(e => e.Description))}");
    }

    public async Task DisposeAsync()
    {
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }

    // ── AC1: Gửi email sau khi đăng ký ───────────────────────────────────────

    [Fact(DisplayName = "AC1: Sau khi đăng ký, hệ thống gọi gửi email xác nhận")]
    public async Task AC1_AfterRegistration_ShouldSendConfirmationEmail()
    {
        // Arrange
        _emailServiceMock
            .Setup(s => s.SendConfirmationEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.GenerateAndSendConfirmationAsync(
            _testUser.Id, _testUser.Email!, _testUser.FullName);

        // Assert: EmailService phải được gọi đúng 1 lần với email đúng
        _emailServiceMock.Verify(
            s => s.SendConfirmationEmailAsync(
                "buyer@example.com",
                "Người Mua Hàng",
                It.Is<string>(link => link.Contains("confirm") && link.Contains(_testUser.Id))),
            Times.Once,
            "AC1 FAIL: Email xác nhận phải được gửi ngay sau khi đăng ký");
    }

    // ── AC2: Email chứa link xác nhận hợp lệ ────────────────────────────────

    [Fact(DisplayName = "AC2: Email chứa liên kết xác nhận hợp lệ có token và userId")]
    public async Task AC2_ConfirmationEmail_ShouldContainValidLinkWithToken()
    {
        // Arrange
        string? capturedLink = null;
        _emailServiceMock
            .Setup(s => s.SendConfirmationEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((_, _, link) => capturedLink = link)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.GenerateAndSendConfirmationAsync(
            _testUser.Id, _testUser.Email!, _testUser.FullName);

        // Assert
        Assert.NotNull(capturedLink);
        Assert.Contains("userId=", capturedLink);
        Assert.Contains("token=", capturedLink);
        Assert.Contains(_testUser.Id, capturedLink);
        Assert.StartsWith("https://", capturedLink);
    }

    // ── AC3: Xác nhận thành công → tài khoản kích hoạt ──────────────────────

    [Fact(DisplayName = "AC3: Xác nhận token hợp lệ → EmailConfirmed = true")]
    public async Task AC3_ValidToken_ShouldActivateAccount()
    {
        // Arrange: Tạo token hợp lệ từ Identity
        var rawToken = await _userManager.GenerateEmailConfirmationTokenAsync(_testUser);
        var encodedToken = Microsoft.AspNetCore.WebUtilities.WebEncoders
            .Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(rawToken));

        // Act
        var (success, message) = await _sut.ConfirmEmailAsync(_testUser.Id, encodedToken);

        // Assert
        Assert.True(success, $"AC3 FAIL: {message}");

        var updatedUser = await _userManager.FindByIdAsync(_testUser.Id);
        Assert.True(updatedUser!.EmailConfirmed,
            "AC3 FAIL: EmailConfirmed phải = true sau khi xác nhận thành công");
    }

    // ── AC4: Tài khoản chưa xác nhận không được kích hoạt ───────────────────

    [Fact(DisplayName = "AC4: Tài khoản mới tạo có EmailConfirmed = false")]
    public async Task AC4_NewAccount_ShouldHaveEmailConfirmedFalse()
    {
        var isConfirmed = await _sut.IsEmailConfirmedAsync(_testUser.Id);
        Assert.False(isConfirmed,
            "AC4 FAIL: Tài khoản mới đăng ký phải có EmailConfirmed = false");
    }

    [Fact(DisplayName = "AC4: Identity từ chối sign-in nếu email chưa xác nhận")]
    public async Task AC4_UnconfirmedAccount_ShouldBeBlockedByIdentity()
    {
        var signInManager = _serviceProvider
            .GetRequiredService<SignInManager<ApplicationUser>>();

        var result = await signInManager.CheckPasswordSignInAsync(
            _testUser, "P@ssw0rd!", lockoutOnFailure: false);

        Assert.False(result.Succeeded,
            "AC4 FAIL: Tài khoản chưa xác nhận phải bị chặn đăng nhập");
        Assert.True(result.IsNotAllowed,
            "AC4 FAIL: Lý do chặn phải là 'IsNotAllowed' (chưa xác nhận email)");
    }

    // ── AC5: Token không hợp lệ → từ chối kích hoạt ─────────────────────────

    [Fact(DisplayName = "AC5: Token giả mạo → từ chối kích hoạt")]
    public async Task AC5_FakeToken_ShouldRejectActivation()
    {
        var fakeToken = "invalid-token-xyz-123";
        var (success, _) = await _sut.ConfirmEmailAsync(_testUser.Id, fakeToken);

        Assert.False(success, "AC5 FAIL: Token không hợp lệ phải bị từ chối");
        var user = await _userManager.FindByIdAsync(_testUser.Id);
        Assert.False(user!.EmailConfirmed,
            "AC5 FAIL: EmailConfirmed phải vẫn = false khi token không hợp lệ");
    }

    [Fact(DisplayName = "AC5: UserId không tồn tại → từ chối kích hoạt")]
    public async Task AC5_NonExistentUserId_ShouldRejectActivation()
    {
        var (success, _) = await _sut.ConfirmEmailAsync(
            "non-existent-user-id", "any-token");

        Assert.False(success,
            "AC5 FAIL: UserId không tồn tại phải bị từ chối");
    }

    [Fact(DisplayName = "AC5: Token đúng định dạng nhưng sai nội dung → từ chối")]
    public async Task AC5_WrongTokenContent_ShouldRejectActivation()
    {
        var wrongContent = "This is a wrong token content!";
        var wrongToken = Microsoft.AspNetCore.WebUtilities.WebEncoders
            .Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(wrongContent));

        var (success, message) = await _sut.ConfirmEmailAsync(_testUser.Id, wrongToken);

        Assert.False(success,
            $"AC5 FAIL: Token sai nội dung phải bị từ chối. Message: {message}");
    }
}
