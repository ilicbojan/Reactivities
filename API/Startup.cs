using System.Text;
using System.Threading.Tasks;
using Application.Activities;
using Application.Interfaces;
using API.Middleware;
using API.SignalR;
using AutoMapper;
using Domain;
using FluentValidation.AspNetCore;
using Infrastructure.Photos;
using Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Persistence;
using Application.Profiles;
using System;

namespace API
{
  public class Startup
  {
    public Startup(IConfiguration configuration)
    {
      Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureDevelopmentServices(IServiceCollection services)
    {
      // Dodavanje DbContext klase, baze podataka
      // prebaceno iz ConfigureServices metoda
      services.AddDbContext<DataContext>(opt =>
      {
        opt.UseLazyLoadingProxies();
        opt.UseSqlite(Configuration.GetConnectionString("DefaultConnection"));
      });

      ConfigureServices(services);
    }


    public void ConfigureProductionServices(IServiceCollection services)
    {
      // Dodavanje DbContext klase, baze podataka
      // prebaceno iz ConfigureServices metoda
      services.AddDbContext<DataContext>(opt =>
      {
        opt.UseLazyLoadingProxies();
        opt.UseSqlite(Configuration.GetConnectionString("DefaultConnection"));
        // opt.UseSqlServer(Configuration.GetConnectionString("DefaultConnection"));
        // opt.UseMySql(Configuration.GetConnectionString("DefaultConnection"));
      });

      ConfigureServices(services);
    }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
      // Dodavanje CORS, cross origin, da client-app moze da komunicira sa API
      services.AddCors(opt =>
      {
        opt.AddPolicy("CorsPolicy", policy =>
        {
          policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")
            .WithOrigins("http://localhost:3000")
            .AllowCredentials();
        });
      });
      // dovoljno da se kaze samo za jedan handler, AddMediatR trazi samo assembly
      services.AddMediatR(typeof(List.Handler).Assembly);
      services.AddAutoMapper(typeof(List.Handler));
      services.AddSignalR();
      // AddFluentValidation za validaciju propertija
      services.AddControllers(opt =>
        {
          // dodavanje autorizacije za svaki controller
          var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
          opt.Filters.Add(new AuthorizeFilter(policy));
        })
        .AddFluentValidation(cfg => cfg.RegisterValidatorsFromAssemblyContaining<Create>())
        // Microsoft.AspNetCore.Mvc.NewtonsoftJson u Application project - zbog greske: A possible object cycle was detected which is not supported. This can either be due to a cycle or if the object depth is larger than the maximum allowed depth of 32
        .AddNewtonsoftJson(options =>
          options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore
        );

      // PROBLEM SA OVIM KODOM, dodavanje identity
      // var builder = services.AddIdentityCore<AppUser>();
      // var identityBuilder = new IdentityBuilder(builder.UserType, builder.Services);
      // identityBuilder.AddEntityFrameworkStores<DataContext>();
      // identityBuilder.AddSignInManager<SignInManager<AppUser>>();

      // zamena za prosli kod, dodavanje Identity 
      services.AddDefaultIdentity<AppUser>().AddEntityFrameworkStores<DataContext>();

      services.AddAuthorization(opt =>
      {
        opt.AddPolicy("IsActivityHost", policy =>
        {
          policy.Requirements.Add(new IsHostRequirement());
        });
      });
      services.AddTransient<IAuthorizationHandler, IsHostRequirementHandler>();

      // dodavanje autentifikacije
      var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["TokenKey"]));
      services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opt =>
        {
          opt.TokenValidationParameters = new TokenValidationParameters
          {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateAudience = false,
            ValidateIssuer = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
          };
          // za SignalR
          opt.Events = new JwtBearerEvents
          {
            OnMessageReceived = context =>
            {
              var accessToken = context.Request.Query["access_token"];
              var path = context.HttpContext.Request.Path;
              if (!string.IsNullOrEmpty(accessToken) && (path.StartsWithSegments("/chat")))
              {
                context.Token = accessToken;
              }
              return Task.CompletedTask;
            }
          };
        });

      // dodato za validaciju tokenom
      services.AddScoped<IJwtGenerator, JwtGenerator>();
      services.AddScoped<IUserAccessor, UserAccessor>();
      services.AddScoped<IPhotoAccessor, PhotoAccessor>();
      services.AddScoped<IProfileReader, ProfileReader>();
      services.Configure<CloudinarySettings>(Configuration.GetSection("Cloudinary"));
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
      app.UseMiddleware<ErrorHandlingMiddleware>();
      if (env.IsDevelopment())
      {
        //app.UseDeveloperExceptionPage();
      }

      // iskljuceno dok se radi u Development, da sajt ne bi pitao za sigurnost
      // izbrisati iz launchSettings.json API: "applicationUrl": https://...., ostaviti samo http
      // OBAVEZNO VRATITI KAD JE PUBLISH
      // app.UseHttpsRedirection();

      app.UseHsts(hsts => hsts.MaxAge(365).IncludeSubdomains());
      app.UseXContentTypeOptions();
      app.UseReferrerPolicy(opt => opt.NoReferrer());
      app.UseXXssProtection(opt => opt.EnabledWithBlockMode());
      app.UseXfo(opt => opt.Deny());
      app.UseRedirectValidation();
      app.UseCsp(opt => opt
          .BlockAllMixedContent()
          .StyleSources(s => s.Self().CustomSources("https://fonts.googleapis.com", "sha256-F4GpCPyRepgP5znjMD8sc7PEjzet5Eef4r09dEGPpTs="))
          .StyleSources(s => s.UnsafeInline())
          .FontSources(s => s.Self().CustomSources("https://fonts.gstatic.com"))
          .FormActions(s => s.Self())
          .FrameAncestors(s => s.Self())
          .ImageSources(s => s.Self().CustomSources("https://res.cloudinary.com", "blob:", "data:"))
          .ScriptSources(s => s.Self().CustomSources("sha256-ma5XxS1EBgt17N22Qq31rOxxRWRfzUTQS1KOtfYwuNo="))
        );

      app.UseRouting();
      app.UseCors("CorsPolicy");

      // rad sa static files, css, html, img
      app.UseDefaultFiles();
      app.UseStaticFiles();

      app.UseAuthentication();
      app.UseAuthorization();

      // Vise se ne koristi za 3.1
      // app.UseSignalR(routes => { routes.MapHub<ChatHub>("/chat"); });
      app.UseEndpoints(endpoints =>
      {
        endpoints.MapControllers();
        // dodavanje putanje za chat - SignalR
        endpoints.MapHub<ChatHub>("/chat");
        // dodavanje static file, index iz react app, Fallback controller
        endpoints.MapFallbackToController("Index", "Fallback");
      });
    }
  }
}