using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models;

namespace PRN232_GradingSystem_Worker_Services.Implementations;

public class ApiGradingService : IApiGradingService
{
    private readonly ILogger<ApiGradingService> _logger;

    // Tiêu chí chấm điểm theo bảng
    private readonly Dictionary<string, ApiGradingCriteria> _gradingCriteria = new()
    {
        {
            "Create",
            new ApiGradingCriteria
            {
                Function = "Create (Thêm mới)",
                EndpointPattern = "POST /{entityName}",
                MaxScore = 1.0,
                Notes = new List<string>
                {
                    "Thêm dữ liệu hợp lệ",
                    "Hiển thị đầu danh sách",
                    "Có validation đầy đủ"
                }
            }
        },
        {
            "Update",
            new ApiGradingCriteria
            {
                Function = "Update (Cập nhật)",
                EndpointPattern = "PUT /{entityName}",
                MaxScore = 1.0,
                Notes = new List<string>
                {
                    "Cập nhật đúng bản ghi",
                    "Kiểm tra dữ liệu hợp lệ"
                }
            }
        },
        {
            "Delete",
            new ApiGradingCriteria
            {
                Function = "Delete (Xóa)",
                EndpointPattern = "DELETE /{entityName}/{id}",
                MaxScore = 0.5,
                Notes = new List<string>
                {
                    "Xóa đúng đối tượng",
                    "Xử lý lỗi khi không tồn tại"
                }
            }
        },
        {
            "GetAll",
            new ApiGradingCriteria
            {
                Function = "Get All (Danh sách)",
                EndpointPattern = "GET /{entityName}",
                MaxScore = 1.0,
                Notes = new List<string>
                {
                    "Hiển thị đầy đủ danh sách",
                    "Có thông tin liên quan (nếu có join bảng khác)"
                }
            }
        },
        {
            "GetById",
            new ApiGradingCriteria
            {
                Function = "Get by ID (Chi tiết)",
                EndpointPattern = "GET /{entityName}/{id}",
                MaxScore = 0.5,
                Notes = new List<string>
                {
                    "Hiển thị đúng chi tiết theo ID"
                }
            }
        },
        {
            "Search",
            new ApiGradingCriteria
            {
                Function = "Search (Tìm kiếm có phân trang)",
                EndpointPattern = "POST /{entityName}/Search",
                MaxScore = 2.0,
                Notes = new List<string>
                {
                    "Tìm theo nhiều điều kiện",
                    "Có phân trang",
                    "Trả đúng JSON format (totalItems, totalPages, items, ...)"
                }
            }
        }
    };

    public ApiGradingService(ILogger<ApiGradingService> logger)
    {
        _logger = logger;
    }

    public async Task<ApiGradingResultResponse> GradeApiAsync(string projectPath, string entityName, CancellationToken cancellationToken = default)
    {
        var result = new ApiGradingResultResponse
        {
            EntityName = entityName,
            MaxScore = _gradingCriteria.Values.Sum(c => c.MaxScore),
            EndpointScores = new List<EndpointScoreResponse>()
        };

        try
        {
            // Nếu entityName rỗng, tự động detect từ controller
            if (string.IsNullOrWhiteSpace(entityName))
            {
                entityName = DetectEntityName(projectPath);
                result.EntityName = entityName;
                _logger.LogInformation("Auto-detected entity name: {EntityName}", entityName);
            }

            // Bước 1: Build project (đảm bảo build thành công)
            var buildResult = await BuildProjectAsync(projectPath, cancellationToken);
            result.BuildStatus = buildResult.Success ? "Success" : "Failed";
            result.BuildError = buildResult.ErrorMessage;

            if (!buildResult.Success)
            {
                _logger.LogWarning("Build failed: {Error}", buildResult.ErrorMessage);
                return result;
            }

            // Bước 2: Tìm controller
            _logger.LogInformation("[ApiGradingService] Searching for controller with entity name: {EntityName}", entityName);
            var controllerPath = FindController(projectPath, entityName);
            if (string.IsNullOrEmpty(controllerPath))
            {
                _logger.LogWarning("[ApiGradingService] Controller not found for entity: {EntityName}", entityName);
                _logger.LogInformation("[ApiGradingService] Searched pattern: {EntityName}Controller.cs", entityName);
                // Vẫn chấm điểm nhưng tất cả endpoints sẽ fail
            }
            else
            {
                _logger.LogInformation("[ApiGradingService] Found controller: {ControllerPath}", controllerPath);
                var relativePath = Path.GetRelativePath(projectPath, controllerPath);
                _logger.LogInformation("[ApiGradingService] Controller relative path: {RelativePath}", relativePath);
            }

            // Bước 3: Chấm điểm từng endpoint
            foreach (var criteriaKey in _gradingCriteria.Keys)
            {
                var criteria = _gradingCriteria[criteriaKey];
                var endpointScore = await GradeEndpointAsync(
                    criteriaKey,
                    criteria,
                    entityName,
                    controllerPath,
                    projectPath,
                    cancellationToken);

                result.EndpointScores.Add(endpointScore);
            }

            result.TotalScore = result.EndpointScores.Sum(e => e.Score);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during API grading");
            result.BuildStatus = "Error";
            result.BuildError = ex.Message;
        }

        return result;
    }

    private string DetectEntityName(string projectPath)
    {
        // Tìm tất cả controller files
        var controllerFiles = Directory.GetFiles(projectPath, "*Controller.cs", SearchOption.AllDirectories);
        
        if (controllerFiles.Length == 0)
        {
            return "Unknown";
        }

        // Lấy controller đầu tiên và extract entity name
        var firstController = controllerFiles[0];
        var fileName = Path.GetFileNameWithoutExtension(firstController);
        
        // Remove "Controller" suffix
        if (fileName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return fileName.Substring(0, fileName.Length - "Controller".Length);
        }

        return fileName;
    }

    private async Task<BuildResult> BuildProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        // Tìm file .csproj hoặc .sln
        var csprojFiles = Directory.GetFiles(projectPath, "*.csproj", SearchOption.AllDirectories);
        var slnFiles = Directory.GetFiles(projectPath, "*.sln", SearchOption.AllDirectories);

        if (slnFiles.Length > 0)
        {
            return await BuildSolutionAsync(slnFiles[0], cancellationToken);
        }
        else if (csprojFiles.Length > 0)
        {
            return await BuildProjectFileAsync(csprojFiles[0], cancellationToken);
        }

        return new BuildResult { Success = false, ErrorMessage = "No .csproj or .sln file found" };
    }

    private async Task<BuildResult> BuildSolutionAsync(string solutionPath, CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{solutionPath}\" -c Release",
                WorkingDirectory = Path.GetDirectoryName(solutionPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return new BuildResult { Success = false, ErrorMessage = "Failed to start build process" };
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return new BuildResult { Success = true };
            }

            return new BuildResult
            {
                Success = false,
                ErrorMessage = $"Build failed with exit code {process.ExitCode}. Output: {output}. Error: {error}"
            };
        }
        catch (Exception ex)
        {
            return new BuildResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task<BuildResult> BuildProjectFileAsync(string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" -c Release",
                WorkingDirectory = Path.GetDirectoryName(projectPath),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                return new BuildResult { Success = false, ErrorMessage = "Failed to start build process" };
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return new BuildResult { Success = true };
            }

            return new BuildResult
            {
                Success = false,
                ErrorMessage = $"Build failed with exit code {process.ExitCode}. Output: {output}. Error: {error}"
            };
        }
        catch (Exception ex)
        {
            return new BuildResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private string? FindController(string projectPath, string entityName)
    {
        // Tìm controller theo tên entity
        var controllerName = $"{entityName}Controller.cs";
        var controllerFiles = Directory.GetFiles(projectPath, controllerName, SearchOption.AllDirectories);

        if (controllerFiles.Length > 0)
        {
            return controllerFiles[0];
        }

        // Tìm tất cả controller files
        var allControllers = Directory.GetFiles(projectPath, "*Controller.cs", SearchOption.AllDirectories);
        foreach (var controller in allControllers)
        {
            var content = File.ReadAllText(controller);
            // Kiểm tra xem controller có chứa entity name không
            if (content.Contains(entityName, StringComparison.OrdinalIgnoreCase))
            {
                return controller;
            }
        }

        return null;
    }

    private async Task<EndpointScoreResponse> GradeEndpointAsync(
        string criteriaKey,
        ApiGradingCriteria criteria,
        string entityName,
        string? controllerPath,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var endpointScore = new EndpointScoreResponse
        {
            Function = criteria.Function,
            Endpoint = criteria.EndpointPattern.Replace("{entityName}", entityName),
            MaxScore = criteria.MaxScore,
            CriteriaChecks = new List<CriteriaCheckResponse>()
        };

        // Kiểm tra endpoint có tồn tại không
        if (string.IsNullOrEmpty(controllerPath))
        {
            endpointScore.EndpointExists = false;
            endpointScore.ErrorMessage = "Controller not found";
            endpointScore.Score = 0;
            return endpointScore;
        }

        var controllerContent = File.ReadAllText(controllerPath);
        _logger.LogInformation("[ApiGradingService] Checking endpoint: {CriteriaKey} for {EntityName}", criteriaKey, entityName);
        
        endpointScore.EndpointExists = CheckEndpointExists(controllerContent, criteriaKey, entityName);

        if (!endpointScore.EndpointExists)
        {
            _logger.LogWarning("[ApiGradingService] Endpoint {CriteriaKey} NOT FOUND in controller", criteriaKey);
            _logger.LogInformation("[ApiGradingService] Expected pattern: {EndpointPattern}", endpointScore.Endpoint);
            endpointScore.Score = 0;
            endpointScore.ErrorMessage = "Endpoint not found";
            return endpointScore;
        }
        
        _logger.LogInformation("[ApiGradingService] Endpoint {CriteriaKey} FOUND - checking criteria...", criteriaKey);

        // Kiểm tra từng ghi chú
        var totalNotes = criteria.Notes.Count;
        var passedNotes = 0;

        foreach (var note in criteria.Notes)
        {
            var check = CheckCriteria(controllerContent, criteriaKey, note, entityName, projectPath);
            endpointScore.CriteriaChecks.Add(check);
            
            _logger.LogInformation("[ApiGradingService]   Criteria '{Note}': {Status}", 
                note, 
                check.Passed ? "PASS" : "FAIL");
            if (!check.Passed && !string.IsNullOrEmpty(check.Details))
            {
                _logger.LogInformation("[ApiGradingService]     Reason: {Details}", check.Details);
            }

            if (check.Passed)
            {
                passedNotes++;
            }
        }
        
        _logger.LogInformation("[ApiGradingService] Endpoint {CriteriaKey}: {PassedNotes}/{TotalNotes} criteria passed", 
            criteriaKey, passedNotes, totalNotes);

        // Tính điểm: nếu thiếu ghi chú thì trừ theo %
        var noteScoreRatio = totalNotes > 0 ? (double)passedNotes / totalNotes : 0;
        endpointScore.Score = criteria.MaxScore * noteScoreRatio;

        return endpointScore;
    }

    private bool CheckEndpointExists(string controllerContent, string criteriaKey, string entityName)
    {
        try
        {
            _logger.LogWarning("[ApiGradingService] ===== CHECKING ENDPOINT: {CriteriaKey} for entity: {EntityName} =====", criteriaKey, entityName);
            
            // Extract class-level route (có thể có [Route("[controller]")])
            var classRoute = ExtractClassRoute(controllerContent, entityName);
            _logger.LogWarning("[ApiGradingService] Class-level route: {ClassRoute}", classRoute ?? "(none)");
            
            // Extract tất cả routes từ methods
            var methodRoutes = ExtractMethodRoutes(controllerContent, classRoute, entityName);
            _logger.LogWarning("[ApiGradingService] Total routes extracted: {Count}", methodRoutes.Count);
            
            // Log tất cả routes để debug
            foreach (var route in methodRoutes)
            {
                _logger.LogWarning("[ApiGradingService] Found route: {HttpMethod} {Route}", route.HttpMethod, route.Path);
            }
            
            if (methodRoutes.Count == 0)
            {
                _logger.LogWarning("[ApiGradingService] WARNING: No routes extracted from controller!");
            }
        
            // Check route pattern dựa trên criteriaKey
            var result = criteriaKey switch
            {
                "Create" => methodRoutes.Any(r => 
                {
                    var matches = r.HttpMethod == "POST" && 
                        !r.Path.Contains("/search", StringComparison.OrdinalIgnoreCase) &&
                        !r.Path.Contains("/filter", StringComparison.OrdinalIgnoreCase) &&
                        !r.Path.Contains("{id}") &&
                        MatchesEntityRoute(r.Path, entityName, exact: true);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ Create endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                "Update" => methodRoutes.Any(r => 
                {
                    var matches = r.HttpMethod == "PUT" && 
                        r.Path.Contains("{id}") &&
                        MatchesEntityRoute(r.Path, entityName, exact: false);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ Update endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                "Delete" => methodRoutes.Any(r => 
                {
                    var matches = r.HttpMethod == "DELETE" && 
                        r.Path.Contains("{id}") &&
                        MatchesEntityRoute(r.Path, entityName, exact: false);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ Delete endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                "GetAll" => methodRoutes.Any(r => 
                {
                    var matches = r.HttpMethod == "GET" && 
                        !r.Path.Contains("{id}") &&
                        !r.Path.Contains("/search", StringComparison.OrdinalIgnoreCase) &&
                        !r.Path.Contains("/filter", StringComparison.OrdinalIgnoreCase) &&
                        MatchesEntityRoute(r.Path, entityName, exact: true);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ GetAll endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                "GetById" => methodRoutes.Any(r => 
                {
                    var matches = r.HttpMethod == "GET" && 
                        r.Path.Contains("{id}") &&
                        !r.Path.Contains("/search", StringComparison.OrdinalIgnoreCase) &&
                        !r.Path.Contains("/filter", StringComparison.OrdinalIgnoreCase) &&
                        MatchesEntityRoute(r.Path, entityName, exact: false);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ GetById endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                "Search" => methodRoutes.Any(r => 
                {
                    var matches = (r.HttpMethod == "POST" || r.HttpMethod == "GET") && 
                        (r.Path.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
                         r.Path.Contains("/filter", StringComparison.OrdinalIgnoreCase)) &&
                        MatchesEntityRoute(r.Path, entityName, exact: false);
                    if (matches)
                    {
                        _logger.LogWarning("[ApiGradingService] ✓ Search endpoint MATCHED: {Route}", r.Path);
                    }
                    return matches;
                }),
                _ => false
            };
            
            _logger.LogWarning("[ApiGradingService] Endpoint {CriteriaKey} check result: {Result}", criteriaKey, result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ApiGradingService] ERROR checking endpoint {CriteriaKey} for entity {EntityName}: {Message}", 
                criteriaKey, entityName, ex.Message);
            _logger.LogError(ex, "[ApiGradingService] Stack trace: {StackTrace}", ex.StackTrace);
            return false;
        }
    }
    
    private string? ExtractClassRoute(string controllerContent, string entityName)
    {
        // Tìm [Route("...")] ở class level (trước các methods)
        // Tìm tất cả [Route("...")] trong file, lấy cái đầu tiên sau class declaration
        var lines = controllerContent.Split('\n');
        var foundClass = false;
        
        foreach (var line in lines)
        {
            // Detect class start
            if (line.Contains("class") && line.Contains("Controller"))
            {
                foundClass = true;
                continue;
            }
            
            if (foundClass)
            {
                // Tìm [Route("...")] sau class declaration
                if (line.Contains("[Route("))
                {
                    // Thử nhiều pattern để match
                    var patterns = new[]
                    {
                        @"\[Route\s*\(\s*[""']([^""']+)[""']\s*\)\]",  // [Route("api/")]
                        @"\[Route\s*\(\s*@?""([^""]+)""\s*\)\]",        // [Route(@"api/")]
                        @"\[Route\s*\(\s*'([^']+)'\s*\)\]"              // [Route('api/')]
                    };
                    
                    foreach (var pattern in patterns)
                    {
                        var routeMatch = System.Text.RegularExpressions.Regex.Match(
                            line, 
                            pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        if (routeMatch.Success)
                        {
                            var route = routeMatch.Groups[1].Value;
                            _logger.LogWarning("[ApiGradingService] Found class route: '{Route}' from line: {Line}", route, line.Trim());
                            
                            // Replace [controller] placeholder với entity name
                            route = route.Replace("[controller]", entityName, StringComparison.OrdinalIgnoreCase);
                            return route.TrimEnd('/');
                        }
                    }
                }
                
                // Nếu đã vào method đầu tiên (có public/private/protected), dừng lại
                if (line.Contains("public") || line.Contains("private") || line.Contains("protected"))
                {
                    if (line.Contains("Task") || line.Contains("IActionResult") || line.Contains("ActionResult") || line.Contains("void"))
                    {
                        break; // Đã vào method đầu tiên
                    }
                }
            }
        }
        
        _logger.LogWarning("[ApiGradingService] No class-level route found");
        return null;
    }
    
    private List<MethodRoute> ExtractMethodRoutes(string controllerContent, string? classRoute, string entityName)
    {
        var routes = new List<MethodRoute>();
        var methods = ExtractMethods(controllerContent);
        _logger.LogWarning("[ApiGradingService] Extracted {Count} methods from controller", methods.Count);
        
        for (int i = 0; i < methods.Count; i++)
        {
            var method = methods[i];
            _logger.LogWarning("[ApiGradingService] Processing method {Index}/{Total}. Method preview (first 300 chars): {Preview}", 
                i + 1, methods.Count, method.Length > 300 ? method.Substring(0, 300) + "..." : method);
            
            // Extract HTTP method và route từ method
            var httpMethod = ExtractHttpMethod(method);
            if (httpMethod == null)
            {
                _logger.LogWarning("[ApiGradingService] Method {Index} has no HTTP verb attribute. Checking method content...", i + 1);
                _logger.LogWarning("[ApiGradingService] Method {Index} contains [HttpPost]: {HasPost}, [HttpGet]: {HasGet}, [HttpPut]: {HasPut}, [HttpDelete]: {HasDelete}", 
                    i + 1, 
                    method.Contains("[HttpPost]"), 
                    method.Contains("[HttpGet]"), 
                    method.Contains("[HttpPut]"), 
                    method.Contains("[HttpDelete]"));
                continue;
            }
            
            _logger.LogWarning("[ApiGradingService] Method {Index} has HTTP verb: {HttpMethod}", i + 1, httpMethod);
            
            var methodRoute = ExtractMethodRoute(method, entityName);
            
            // Nếu method không có route nhưng class có route, vẫn sử dụng class route
            // Ví dụ: [Route("[controller]")] ở class + [HttpPost] ở method → route = "Handbags"
            if (methodRoute == null)
            {
                if (string.IsNullOrEmpty(classRoute))
                {
                    _logger.LogWarning("[ApiGradingService] Method {Index} ({HttpMethod}) has no route attribute and no class route. Method preview: {Preview}", 
                        i + 1, httpMethod, method.Length > 200 ? method.Substring(0, 200) + "..." : method);
                    continue;
                }
                else
                {
                    _logger.LogWarning("[ApiGradingService] Method {Index} ({HttpMethod}) has no route attribute, using class route: {ClassRoute}", 
                        i + 1, httpMethod, classRoute);
                    methodRoute = string.Empty; // Sử dụng class route
                }
            }
            
            // Combine class route và method route
            var fullRoute = CombineRoutes(classRoute, methodRoute);
            
            _logger.LogWarning("[ApiGradingService] Extracted route: {HttpMethod} {FullRoute} (methodRoute: {MethodRoute}, classRoute: {ClassRoute})", 
                httpMethod, fullRoute, methodRoute, classRoute ?? "(none)");
            
            routes.Add(new MethodRoute
            {
                HttpMethod = httpMethod,
                Path = fullRoute
            });
        }
        
        return routes;
    }
    
    private string? ExtractHttpMethod(string methodContent)
    {
        // Check với case-insensitive và có thể có thêm ký tự sau [HttpXXX]
        // Ví dụ: [HttpPost("route")], [HttpGet], [HttpPost]
        var normalized = methodContent.Replace("\r", "").Replace("\n", " ");
        
        if (normalized.Contains("[HttpPost", StringComparison.OrdinalIgnoreCase)) return "POST";
        if (normalized.Contains("[HttpGet", StringComparison.OrdinalIgnoreCase)) return "GET";
        if (normalized.Contains("[HttpPut", StringComparison.OrdinalIgnoreCase)) return "PUT";
        if (normalized.Contains("[HttpDelete", StringComparison.OrdinalIgnoreCase)) return "DELETE";
        if (normalized.Contains("[HttpPatch", StringComparison.OrdinalIgnoreCase)) return "PATCH";
        
        return null;
    }
    
    private string? ExtractMethodRoute(string methodContent, string entityName)
    {
        // Tìm route trong HTTP verb attributes: [HttpPost("route")], [HttpGet("route")]
        // Thử nhiều pattern để match các trường hợp khác nhau
        var httpVerbPatterns = new[]
        {
            @"\[HttpPost\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            @"\[HttpGet\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            @"\[HttpPut\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            @"\[HttpDelete\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            @"\[HttpPatch\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            // Thử với @"" verbatim string
            @"\[HttpPost\s*\(\s*@?""([^""]+)""\s*\)\]",
            @"\[HttpGet\s*\(\s*@?""([^""]+)""\s*\)\]",
            @"\[HttpPut\s*\(\s*@?""([^""]+)""\s*\)\]",
            @"\[HttpDelete\s*\(\s*@?""([^""]+)""\s*\)\]",
            @"\[HttpPatch\s*\(\s*@?""([^""]+)""\s*\)\]"
        };
        
        foreach (var pattern in httpVerbPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                methodContent, 
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (match.Success)
            {
                var route = match.Groups[1].Value;
                _logger.LogWarning("[ApiGradingService] Found route in HTTP verb: '{Route}' (pattern matched)", route);
                
                // Replace [controller] placeholder
                route = route.Replace("[controller]", entityName, StringComparison.OrdinalIgnoreCase);
                return route.TrimEnd('/');
            }
        }
        
        // Nếu không có route trong HTTP verb, check [Route("...")] attribute
        var routeAttrPatterns = new[]
        {
            @"\[Route\s*\(\s*[""']([^""']+)[""']\s*\)\]",
            @"\[Route\s*\(\s*@?""([^""]+)""\s*\)\]"
        };
        
        foreach (var pattern in routeAttrPatterns)
        {
            var routeAttrMatch = System.Text.RegularExpressions.Regex.Match(
                methodContent,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            if (routeAttrMatch.Success)
            {
                var route = routeAttrMatch.Groups[1].Value;
                _logger.LogWarning("[ApiGradingService] Found route in [Route] attribute: '{Route}'", route);
                
                route = route.Replace("[controller]", entityName, StringComparison.OrdinalIgnoreCase);
                return route.TrimEnd('/');
            }
        }
        
        _logger.LogWarning("[ApiGradingService] No route found in method. Method preview: {Preview}", 
            methodContent.Length > 200 ? methodContent.Substring(0, 200) + "..." : methodContent);
        
        return null;
    }
    
    private string CombineRoutes(string? classRoute, string? methodRoute)
    {
        if (string.IsNullOrEmpty(classRoute) && string.IsNullOrEmpty(methodRoute))
            return string.Empty;
        
        if (string.IsNullOrEmpty(classRoute))
            return methodRoute ?? string.Empty;
        
        if (string.IsNullOrEmpty(methodRoute))
            return classRoute;
        
        // Combine: classRoute + methodRoute
        // Ví dụ: "api/" + "handbags" = "api/handbags"
        // Ví dụ: "api/" + "handbags/" = "api/handbags/"
        var classRouteNormalized = classRoute.TrimEnd('/');
        var methodRouteNormalized = methodRoute.TrimStart('/');
        
        // Giữ trailing slash của method route nếu có
        var hasTrailingSlash = methodRoute.EndsWith('/');
        var combined = classRouteNormalized + "/" + methodRouteNormalized;
        
        return hasTrailingSlash ? combined + "/" : combined;
    }
    
    private bool MatchesEntityRoute(string route, string entityName, bool exact)
    {
        // Normalize route: lowercase
        var normalizedRoute = route.ToLowerInvariant();
        var normalizedEntity = entityName.ToLowerInvariant();
        
        // Remove query parameters nếu có
        var routePath = normalizedRoute.Split('?')[0];
        
        // Lưu ý: Logic này KHÔNG yêu cầu "api/" prefix
        // Chỉ check entity name trong route, bất kể prefix là gì
        
        if (exact)
        {
            // Exact match: route phải kết thúc bằng entity name (không có {id} hoặc /search)
            // Ví dụ: "api/handbags", "handbags", "v1/handbags", "api/handbags/" đều match với entity "handbags"
            // Không quan tâm prefix "api/", chỉ check last segment
            var segments = routePath.Trim('/').Split('/');
            var nonEmptySegments = segments.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            
            if (nonEmptySegments.Length == 0)
                return false;
            
            var lastSegment = nonEmptySegments[nonEmptySegments.Length - 1];
            
            // Check last segment matches entity name (không check prefix)
            var matches = lastSegment == normalizedEntity;
            
            _logger.LogInformation("[ApiGradingService] Exact match check: route='{Route}' entity='{Entity}' lastSegment='{LastSegment}' → {Result}", 
                route, normalizedEntity, lastSegment, matches);
            
            return matches;
        }
        else
        {
            // Partial match: route chứa entity name (có thể có {id} hoặc /search)
            // Ví dụ: "api/handbags/{id}", "handbags/{id}", "v1/handbags/search" đều match với entity "handbags"
            // Không quan tâm prefix "api/", chỉ check có segment nào chứa entity name
            var segments = routePath.Trim('/').Split('/');
            var nonEmptySegments = segments.Where(s => !string.IsNullOrEmpty(s)).ToArray();
            
            // Check nếu có segment nào chứa entity name (bỏ qua {id} và các placeholder khác)
            var matches = nonEmptySegments.Any(s => 
                s == normalizedEntity || 
                (s.Contains(normalizedEntity) && !s.Contains("{") && !s.Contains("}")));
            
            _logger.LogInformation("[ApiGradingService] Partial match check: route='{Route}' entity='{Entity}' → {Result}", 
                route, normalizedEntity, matches);
            
            return matches;
        }
    }
    
    private class MethodRoute
    {
        public string HttpMethod { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private List<string> ExtractMethods(string controllerContent)
    {
        var methods = new List<string>();
        var lines = controllerContent.Split('\n');
        var currentMethod = new List<string>();
        var inMethod = false;
        var braceCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            
            // Detect method start: public + Task/IActionResult/ActionResult
            if (line.Contains("public") && (line.Contains("Task") || line.Contains("IActionResult") || line.Contains("ActionResult")))
            {
                // Nếu đang trong method trước đó, lưu lại
                if (inMethod && currentMethod.Count > 0)
                {
                    methods.Add(string.Join("\n", currentMethod));
                }
                
                // Bắt đầu method mới - lùi lại để lấy attributes
                currentMethod.Clear();
                
                // Lùi lại tối đa 10 dòng để tìm attributes
                for (int j = i - 1; j >= 0 && j >= i - 10; j--)
                {
                    var prevLine = lines[j].Trim();
                    // Nếu là attribute (bắt đầu bằng [)
                    if (prevLine.StartsWith("[") && (prevLine.Contains("Http") || prevLine.Contains("Route") || prevLine.Contains("Authorize")))
                    {
                        currentMethod.Insert(0, lines[j]);
                    }
                    // Nếu gặp dòng trống hoặc comment, tiếp tục
                    else if (string.IsNullOrWhiteSpace(prevLine) || prevLine.StartsWith("//"))
                    {
                        continue;
                    }
                    // Nếu gặp dòng khác (field, property, etc.), dừng lại
                    else
                    {
                        break;
                    }
                }
                
                currentMethod.Add(line);
                inMethod = true;
                braceCount = 0;
            }
            else if (inMethod)
            {
                // Đang trong method body
                currentMethod.Add(line);
                braceCount += line.Count(c => c == '{');
                braceCount -= line.Count(c => c == '}');

                if (braceCount == 0 && currentMethod.Count > 1)
                {
                    methods.Add(string.Join("\n", currentMethod));
                    currentMethod.Clear();
                    inMethod = false;
                }
            }
        }

        // Lưu method cuối cùng nếu còn
        if (inMethod && currentMethod.Count > 0)
        {
            methods.Add(string.Join("\n", currentMethod));
        }

        return methods;
    }

    private string ExtractMethodName(string methodContent)
    {
        // Extract method name từ method content
        var lines = methodContent.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("public") && (line.Contains("Task") || line.Contains("IActionResult") || line.Contains("ActionResult")))
            {
                // Tìm tên method sau "public" và trước "("
                var methodStart = line.IndexOf("public");
                if (methodStart >= 0)
                {
                    var methodPart = line.Substring(methodStart);
                    var openParen = methodPart.IndexOf('(');
                    if (openParen > 0)
                    {
                        var methodNamePart = methodPart.Substring(0, openParen);
                        var lastSpace = methodNamePart.LastIndexOf(' ');
                        if (lastSpace >= 0 && lastSpace < methodNamePart.Length - 1)
                        {
                            return methodNamePart.Substring(lastSpace + 1).Trim();
                        }
                    }
                }
            }
        }
        return "Unknown";
    }

    private CriteriaCheckResponse CheckCriteria(
        string controllerContent,
        string criteriaKey,
        string note,
        string entityName,
        string projectPath)
    {
        var check = new CriteriaCheckResponse
        {
            Description = note,
            Passed = false
        };

        switch (criteriaKey)
        {
            case "Create":
                check.Passed = note switch
                {
                    "Thêm dữ liệu hợp lệ" => controllerContent.Contains("Create") || 
                                            controllerContent.Contains("Add") ||
                                            controllerContent.Contains("Post"),
                    "Hiển thị đầu danh sách" => controllerContent.Contains("OrderBy") ||
                                                controllerContent.Contains("OrderByDescending") ||
                                                controllerContent.Contains("FirstOrDefault") ||
                                                controllerContent.Contains("Take(1)"),
                    "Có validation đầy đủ" => controllerContent.Contains("Validation") ||
                                              controllerContent.Contains("ModelState") ||
                                              controllerContent.Contains("Required") ||
                                              controllerContent.Contains("Validate"),
                    _ => false
                };
                break;

            case "Update":
                check.Passed = note switch
                {
                    "Cập nhật đúng bản ghi" => controllerContent.Contains("Update") &&
                                               controllerContent.Contains("{id}") &&
                                               (controllerContent.Contains("Find") || 
                                                controllerContent.Contains("GetById") ||
                                                controllerContent.Contains("FirstOrDefault")),
                    "Kiểm tra dữ liệu hợp lệ" => controllerContent.Contains("Validation") ||
                                                 controllerContent.Contains("ModelState") ||
                                                 controllerContent.Contains("Required") ||
                                                 controllerContent.Contains("Validate"),
                    _ => false
                };
                break;

            case "Delete":
                check.Passed = note switch
                {
                    "Xóa đúng đối tượng" => controllerContent.Contains("Delete") &&
                                           controllerContent.Contains("{id}") &&
                                           (controllerContent.Contains("Find") || 
                                            controllerContent.Contains("GetById") ||
                                            controllerContent.Contains("FirstOrDefault")),
                    "Xử lý lỗi khi không tồn tại" => controllerContent.Contains("NotFound") ||
                                                      (controllerContent.Contains("if") && 
                                                      controllerContent.Contains("null")),
                    _ => false
                };
                break;

            case "GetAll":
                check.Passed = note switch
                {
                    "Hiển thị đầy đủ danh sách" => controllerContent.Contains("GetAll") ||
                                                   controllerContent.Contains("ToList") ||
                                                   controllerContent.Contains("List"),
                    "Có thông tin liên quan (nếu có join bảng khác)" => controllerContent.Contains("Include") ||
                                                                         controllerContent.Contains("Join") ||
                                                                         controllerContent.Contains("ThenInclude"),
                    _ => false
                };
                break;

            case "GetById":
                check.Passed = note switch
                {
                    "Hiển thị đúng chi tiết theo ID" => (controllerContent.Contains("GetById") ||
                                                        controllerContent.Contains("Find") ||
                                                        controllerContent.Contains("FirstOrDefault")) &&
                                                        controllerContent.Contains("{id}"),
                    _ => false
                };
                break;

            case "Search":
                check.Passed = note switch
                {
                    "Tìm theo nhiều điều kiện" => controllerContent.Contains("Search") &&
                                                  (controllerContent.Contains("Where") ||
                                                   controllerContent.Contains("Filter") ||
                                                   controllerContent.Contains("&&") ||
                                                   controllerContent.Contains("||")),
                    "Có phân trang" => controllerContent.Contains("Page") ||
                                      controllerContent.Contains("Skip") ||
                                      controllerContent.Contains("Take") ||
                                      controllerContent.Contains("Paged"),
                    "Trả đúng JSON format (totalItems, totalPages, items, ...)" => CheckSearchResponseFormat(controllerContent, projectPath),
                    _ => false
                };
                break;
        }

        if (!check.Passed)
        {
            check.Details = $"Không tìm thấy bằng chứng đáp ứng tiêu chí: {note}";
        }

        return check;
    }

    private bool CheckSearchResponseFormat(string controllerContent, string projectPath)
    {
        // Tìm response model hoặc DTO
        var responseFiles = Directory.GetFiles(projectPath, "*Response.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(projectPath, "*DTO.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(projectPath, "*Model.cs", SearchOption.AllDirectories));

        foreach (var file in responseFiles)
        {
            var content = File.ReadAllText(file);
            var fileHasTotalItems = content.Contains("TotalItems") || content.Contains("totalItems");
            var fileHasTotalPages = content.Contains("TotalPages") || content.Contains("totalPages");
            var fileHasItems = content.Contains("Items") || content.Contains("items");

            if (fileHasTotalItems && fileHasTotalPages && fileHasItems)
            {
                // Kiểm tra xem controller có sử dụng response này không
                if (controllerContent.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    return true;
                }
            }
        }

        // Kiểm tra trực tiếp trong controller content
        var hasTotalItems = controllerContent.Contains("TotalItems") || controllerContent.Contains("totalItems");
        var hasTotalPages = controllerContent.Contains("TotalPages") || controllerContent.Contains("totalPages");
        var hasItems = controllerContent.Contains("Items") || controllerContent.Contains("items");
        
        return hasTotalItems && hasTotalPages && hasItems;
    }

    private class ApiGradingCriteria
    {
        public string Function { get; set; } = string.Empty;
        public string EndpointPattern { get; set; } = string.Empty;
        public double MaxScore { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private class BuildResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}

