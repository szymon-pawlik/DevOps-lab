using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ProducerAPI.Data;
using ProducerAPI.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;

namespace ProducerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly JobDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(JobDbContext dbContext, IConfiguration configuration, ILogger<AuthController> logger)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("Username, email and password are required");
        }

        if (await _dbContext.Users.AnyAsync(u => u.Username == request.Username))
        {
            return BadRequest("Username already exists");
        }

        if (await _dbContext.Users.AnyAsync(u => u.Email == request.Email))
        {
            return BadRequest("Email already exists");
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, BCrypt.Net.BCrypt.GenerateSalt(12));
        _logger.LogInformation($"Registering user: {request.Username}, PasswordHash length: {passwordHash.Length}, Hash: {passwordHash.Substring(0, Math.Min(20, passwordHash.Length))}...");

        var user = new User
        {
            Username = request.Username,
            Email = request.Email,
            PasswordHash = passwordHash,
            Role = UserRole.User,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation($"User registered successfully: {user.Username}, Role: {user.Role}, Id: {user.Id}");

        var token = GenerateJwtToken(user);

        return Ok(new
        {
            Token = token,
            User = new
            {
                user.Id,
                user.Username,
                user.Email,
                user.Role
            }
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return BadRequest("Username and password are required");
        }

        _logger.LogInformation($"Login attempt for username: {request.Username}");
        
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        
        if (user == null)
        {
            _logger.LogWarning($"User not found: {request.Username}");
            return Unauthorized("Invalid username or password");
        }

        _logger.LogInformation($"User found: {user.Username}, Role: {user.Role}, PasswordHash length: {user.PasswordHash?.Length ?? 0}, Hash preview: {user.PasswordHash?.Substring(0, Math.Min(20, user.PasswordHash?.Length ?? 0))}...");
        
        try
        {
            var passwordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash, false);
            _logger.LogInformation($"Password verification result: {passwordValid}");
            
            if (!passwordValid)
            {
                _logger.LogWarning($"Invalid password for user: {request.Username}");
                return Unauthorized("Invalid username or password");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error verifying password for user: {request.Username}");
            return Unauthorized("Invalid username or password");
        }

        _logger.LogInformation($"Login successful for user: {user.Username}, Role: {user.Role}");

        var token = GenerateJwtToken(user);

        return Ok(new
        {
            Token = token,
            User = new
            {
                user.Id,
                user.Username,
                user.Email,
                user.Role
            }
        });
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var user = await _dbContext.Users
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.Email,
                u.Role
            })
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    private string GenerateJwtToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            _configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured")));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class RegisterRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

