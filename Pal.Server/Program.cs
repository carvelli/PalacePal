using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Pal.Server.Services;

namespace Pal.Server
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddGrpc(o => o.EnableDetailedErrors = true);
            builder.Services.AddDbContext<PalContext>();
            builder.Services.AddHostedService<RemoveIpHashService>();
            builder.Services.AddSingleton<PalaceLocationCache>();
            builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = builder.Configuration["JWT:Issuer"],
                    ValidAudience = builder.Configuration["JWT:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(builder.Configuration["JWT:Key"])),
                };
            });
            builder.Services.AddAuthorization();

            builder.Host.UseSystemd();

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseGrpcWeb();
            app.MapGrpcService<AccountService>().EnableGrpcWeb();
            app.MapGrpcService<PalaceService>().EnableGrpcWeb();

            using (var scope = app.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<PalContext>();
                dbContext.Database.Migrate();
            }
            
            await app.RunAsync();
        }
            
    }
}