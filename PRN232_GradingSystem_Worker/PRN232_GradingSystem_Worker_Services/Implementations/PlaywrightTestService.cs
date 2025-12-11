using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PRN232_GradingSystem_Worker_Services.Interfaces;
using PRN232_GradingSystem_Worker_Services.Models;
using PRN232_GradingSystem_Worker_Services.Settings;

namespace PRN232_GradingSystem_Worker_Services.Implementations
{
    public sealed class PlaywrightTestService : IUITestService
    {
        private readonly ILogger<PlaywrightTestService> _logger;
        private readonly PlaywrightTestSettings _settings;
        private static bool _browsersInstalled = false;
        private static readonly SemaphoreSlim _installSemaphore = new SemaphoreSlim(1, 1);
        private string? _listPageUrl; // Store the list page URL after successful login in Q1
        private string? _loginPageUrl; // Store the login page URL for Q1 test cases
        private bool _q3DisplayTopPassed = false; // Store Q3 Display Top test result for Q5 adjustment
        private TestResultDetail? _testResultDetail; // Store detailed test results

        private static readonly Dictionary<string, string> PantherTypeOptionMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = "Black leopards",
            ["2"] = "Black jaguars",
            ["3"] = "Pumas"
        };

        private static readonly string[] PantherTypeSelectSelectors =
        {
            "select[name='PantherProfile.PantherTypeId']",
            "select[id='PantherProfile_PantherTypeId']",
            "label[for='PantherProfile_PantherTypeId'] ~ select",
            "select[name='PantherTypeId']",
            "select[id='PantherTypeId']",
            "label[for='PantherTypeId'] ~ select",
            "form select"
        };

        public PlaywrightTestService(ILogger<PlaywrightTestService> logger, IOptions<PlaywrightTestSettings> options)
        {
            _logger = logger;
            _settings = options?.Value ?? new PlaywrightTestSettings();
        }

        /// <summary>
        /// Flexible element finder with multiple fallback strategies
        /// </summary>
        private class FlexibleElementFinder
        {
            private readonly IPage _page;
            private readonly ILogger<PlaywrightTestService> _logger;

            public FlexibleElementFinder(IPage page, ILogger<PlaywrightTestService> logger)
            {
                _page = page;
                _logger = logger;
            }

            /// <summary>
            /// Find email input field using multiple strategies
            /// </summary>
            public async Task<ILocator?> FindEmailInputAsync()
            {
                var strategies = new[]
                {
                    "input[type='email']",
                    "input[name*='email' i]",
                    "input[id*='email' i]",
                    "input[placeholder*='email' i]",
                    "input[placeholder*='Email' i]",
                    "input[name*='Email' i]",
                    "input[id*='Email' i]",
                    "input[type='text'][name*='email' i]",
                    "input[type='text'][id*='email' i]",
                    "input:not([type='password']):not([type='submit']):not([type='button'])",
                };

                return await FindElementWithStrategiesAsync(strategies, "email input");
            }

            /// <summary>
            /// Find password input field using multiple strategies
            /// </summary>
            public async Task<ILocator?> FindPasswordInputAsync()
            {
                var strategies = new[]
                {
                    "input[type='password']",
                    "input[name*='password' i]",
                    "input[id*='password' i]",
                    "input[placeholder*='password' i]",
                    "input[placeholder*='Password' i]",
                    "input[name*='Password' i]",
                    "input[id*='Password' i]",
                };

                return await FindElementWithStrategiesAsync(strategies, "password input");
            }

            /// <summary>
            /// Find login button using multiple strategies
            /// </summary>
            public async Task<ILocator?> FindLoginButtonAsync()
            {
                var strategies = new[]
                {
                    "button:has-text('Login')",
                    "button:has-text('login')",
                    "button:has-text('Đăng nhập')",
                    "button:has-text('đăng nhập')",
                    "button[type='submit']",
                    "input[type='submit'][value*='Login' i]",
                    "input[type='submit'][value*='login' i]",
                    "button[id*='login' i]",
                    "button[name*='login' i]",
                    "a:has-text('Login')",
                    "a:has-text('login')",
                    "[role='button']:has-text('Login')",
                    "[role='button']:has-text('login')",
                };

                return await FindElementWithStrategiesAsync(strategies, "login button");
            }

            /// <summary>
            /// Find element by trying multiple selector strategies
            /// </summary>
            private async Task<ILocator?> FindElementWithStrategiesAsync(string[] strategies, string elementName)
            {
                foreach (var strategy in strategies)
                {
                    try
                    {
                        var locator = _page.Locator(strategy).First;
                        var isVisible = await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 });
                        
                        if (isVisible)
                        {
                            _logger.LogDebug("[FlexibleFinder] Found {ElementName} using strategy: {Strategy}", elementName, strategy);
                            return locator;
                        }
                    }
                    catch
                    {
                        // Try next strategy
                        continue;
                    }
                }

                // Last resort: try to find by position (first input for email, second for password, etc.)
                _logger.LogWarning("[FlexibleFinder] Could not find {ElementName} using any strategy. Tried {Count} strategies", elementName, strategies.Length);
                return null;
            }

            /// <summary>
            /// Check if login page exists by looking for common login indicators
            /// </summary>
            public async Task<bool> IsLoginPageAsync()
            {
                var loginIndicators = new[]
                {
                    "text=Login",
                    "text=login",
                    "text=Đăng nhập",
                    "text=đăng nhập",
                    "h1:has-text('Login')",
                    "h1:has-text('login')",
                    "h2:has-text('Login')",
                    "h2:has-text('login')",
                };

                foreach (var indicator in loginIndicators)
                {
                    try
                    {
                        var isVisible = await _page.Locator(indicator).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 });
                        if (isVisible)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                // Also check if we have email and password fields (strong indicator of login page)
                var hasEmail = await FindEmailInputAsync() != null;
                var hasPassword = await FindPasswordInputAsync() != null;
                
                return hasEmail && hasPassword;
            }
        }

        public async Task<(int Total, int Passed, TestResultDetail? Detail)> RunAsync(string baseUrl, CancellationToken cancellationToken)
        {
            // Ensure browsers installed (only once, with logging)
            await EnsureBrowsersInstalledAsync(cancellationToken);

            // Initialize test result detail
            _testResultDetail = new TestResultDetail();

            var total = 6; // Q1..Q6
            var passed = 0;
            double totalScore = 0.0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            using var playwright = await Playwright.CreateAsync();
            
            // Auto-detect headless mode: use headless in Docker/Production, GUI in Development
            var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
                          Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Production";
            
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = isDocker,
                SlowMo = isDocker ? 0 : 200, // No slow-mo in production for faster execution
                Args = new[]
                {
                    "--ignore-certificate-errors",
                    "--allow-insecure-localhost",
                    "--allow-running-insecure-content",
                    "--disable-dev-shm-usage", // Required for Docker
                    "--no-sandbox" // Required for Docker
                }
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                BypassCSP = true,
                RecordVideoDir = "playwright-videos",
                RecordVideoSize = new RecordVideoSize { Width = 1280, Height = 720 }
            });
            var page = await context.NewPageAsync();
            
            // Set shorter default timeouts to avoid waiting too long when page doesn't respond
            page.SetDefaultNavigationTimeout(5000); // 5 seconds for navigation
            page.SetDefaultTimeout(5000); // 5 seconds for other operations

            // Resolve a canonical base URL that actually loads under current dev HTTPS/HTTP setup
            var canonicalBaseUrl = await ResolveCanonicalBaseUrlAsync(page, baseUrl);

            // Q1: Login Test (1.0 point total)
            double q1Score = await RunQ1LoginTest(page, canonicalBaseUrl);
            // TestResultDetail already populated in RunQ1LoginTest
            if (q1Score >= 1.0) passed++;
            totalScore += q1Score;
            _logger.LogInformation("[Playwright] Q1 Score: {Score}/1.0", q1Score);

            // Q2: Panther List Test (1.5 points total) - continues on same screen after Q1
            double q2Score = await RunQ2PantherListTest(page);
            // TestResultDetail already populated in RunQ2PantherListTest
            if (q2Score >= 1.5) passed++;
            totalScore += q2Score;
            _logger.LogInformation("[Playwright] Q2 Score: {Score}/1.5", q2Score);

            // Q3: Panther Create Test (2.5 points total)
            double q3Score = await RunQ3PantherCreateTest(page, canonicalBaseUrl);
            // TestResultDetail already populated in RunQ3PantherCreateTest
            if (q3Score >= 2.5) passed++;
            totalScore += q3Score;
            _logger.LogInformation("[Playwright] Q3 Score: {Score}/2.5", q3Score);

            // Q4: Panther Update Test (2.0 points total)
            double q4Score = await RunQ4PantherUpdateTest(page, canonicalBaseUrl);
            // TestResultDetail already populated in RunQ4PantherUpdateTest
            if (q4Score >= 2.0) passed++;
            totalScore += q4Score;
            _logger.LogInformation("[Playwright] Q4 Score: {Score}/2.0", q4Score);

            // Q5: Search Test (1.5 points total)
            double q5Score = await RunQ5SearchTest(page, canonicalBaseUrl);
            // TestResultDetail already populated in RunQ5SearchTest
            if (q5Score >= 1.5) passed++;
            totalScore += q5Score;
            _logger.LogInformation("[Playwright] Q5 Score: {Score}/1.5", q5Score);

            // Q6: Delete Test with SignalR (1.5 points total)
            double q6Score = await RunQ6DeleteTest(page, canonicalBaseUrl, context);
            // TestResultDetail already populated in RunQ6DeleteTest
            if (q6Score >= 1.5) passed++;
            totalScore += q6Score;
            _logger.LogInformation("[Playwright] Q6 Score: {Score}/1.5", q6Score);

            // Total score summary
            _logger.LogInformation("[Playwright] ========================================");
            _logger.LogInformation("[Playwright] Q1 Score: {Q1Score}/1.0 | Q2 Score: {Q2Score}/1.5 | Q3 Score: {Q3Score}/2.5 | Q4 Score: {Q4Score}/2.0 | Q5 Score: {Q5Score}/1.5 | Q6 Score: {Q6Score}/1.5", 
                q1Score, q2Score, q3Score, q4Score, q5Score, q6Score);
            _logger.LogInformation("[Playwright] Total Score: {TotalScore}/10.0", totalScore);
            _logger.LogInformation("[Playwright] ========================================");

            return (total, passed, _testResultDetail);
        }

        private async Task<double> RunQ1LoginTest(IPage page, string baseUrl)
        {
            double score = 1.0; // Start with full score, deduct for failures
            var finder = new FlexibleElementFinder(page, _logger);
            var failedTestCases = new List<string>(); // Track failed test cases for note
            var loginSettings = _settings.Login ?? new LoginTestSettings();
            var invalidCredentials = loginSettings.InvalidCredential ?? new CredentialSettings();
            var adminCredentials = loginSettings.AdminCredential ?? new CredentialSettings();
            var managerCredentials = loginSettings.ManagerCredential ?? new CredentialSettings();
            var invalidErrorKeywords = (loginSettings.InvalidErrorKeywords?.Count > 0
                ? loginSettings.InvalidErrorKeywords
                : new List<string> { "invalid", "wrong", "incorrect", "error", "failed" });
            var permissionKeywords = (loginSettings.PermissionErrorKeywords?.Count > 0
                ? loginSettings.PermissionErrorKeywords
                : new List<string> { "no permission", "permission" });

            try
            {
                // Step 1: Navigate to login page and verify it's a login page
                var loginUrl = await NavigateToLoginPageAsync(page, baseUrl, finder);
                if (string.IsNullOrWhiteSpace(loginUrl))
                {
                    _logger.LogError("[Playwright] Q1: Unable to determine login page URL. Score set to 0.");
                    return 0;
                }
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                }
                catch
                {
                    // Continue if network idle timeout - page might still be usable
                }

                // Wait a bit for page to fully render
                await page.WaitForTimeoutAsync(1000);

                // Check for login page using flexible finder
                var isLoginPage = await finder.IsLoginPageAsync();
                var emailInput = await finder.FindEmailInputAsync();
                var passwordInput = await finder.FindPasswordInputAsync();

                if (!isLoginPage || emailInput == null || passwordInput == null)
                {
                    _logger.LogWarning("[Playwright] Q1: Login page elements not found. isLoginPage={IsLoginPage}, hasEmail={HasEmail}, hasPassword={HasPassword}", 
                        isLoginPage, emailInput != null, passwordInput != null);
                    return 0;
                }

                // Save login page URL for later use in test cases
                _loginPageUrl = page.Url;
                _logger.LogInformation("[Playwright] Q1: Login page verified - found Email and Password fields. Saved login page URL: {Url}", _loginPageUrl);

                // Step 2: Test invalid credentials (admin/123) - should show "Invalid Email or Password!"
                try
                {
                    // Navigate back to saved login page URL if available
                    if (!string.IsNullOrWhiteSpace(_loginPageUrl))
                    {
                        _logger.LogInformation("[Playwright] Q1: Navigating to saved login page URL before test case 1: {Url}", _loginPageUrl);
                        try
                        {
                            await page.GotoAsync(_loginPageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                            await page.WaitForTimeoutAsync(1000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[Playwright] Q1: Failed to navigate to saved login URL: {Message}, trying fallback", ex.Message);
                            var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                            if (!navigated)
                            {
                                _logger.LogWarning("[Playwright] Q1: Unable to return to login page before invalid credential test.");
                            }
                        }
                    }
                    else
                    {
                        // Fallback to original method if login URL not saved
                        var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                        if (!navigated)
                        {
                            _logger.LogWarning("[Playwright] Q1: Unable to return to login page before invalid credential test.");
                        }
                    }
                    
                    // Re-find elements in case page changed
                    emailInput = await finder.FindEmailInputAsync();
                    passwordInput = await finder.FindPasswordInputAsync();
                    var loginButton = await finder.FindLoginButtonAsync();

                    if (emailInput == null || passwordInput == null || loginButton == null)
                    {
                        _logger.LogWarning("[Playwright] Q1: Login form elements missing during invalid credential test. Score set to 0.");
                        return 0;
                    }
                    else
                    {
                        await emailInput.ClearAsync();
                        await emailInput.FillAsync(invalidCredentials.Email ?? string.Empty);
                        await passwordInput.ClearAsync();
                        await passwordInput.FillAsync(invalidCredentials.Password ?? string.Empty);
                        await loginButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        await page.WaitForTimeoutAsync(1500); // Wait for response

                        // Check for error message - accept any error message containing "Invalid", "Wrong", "Incorrect" or similar words
                        var errorKeywords = invalidErrorKeywords;
                        bool errorFound = false;

                        // First, try to find error message via selectors
                        var errorSelectors = new[]
                        {
                            "[class*='error']",
                            "[class*='Error']",
                            "[role='alert']",
                            "[role='alertdialog']",
                            ".alert-danger",
                            ".error-message",
                            ".validation-summary-errors"
                        };

                        foreach (var selector in errorSelectors)
                        {
                            try
                            {
                                var errorElement = page.Locator(selector).First;
                                if (await errorElement.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                                {
                                    var errorText = await errorElement.TextContentAsync() ?? string.Empty;
                                if (errorKeywords.Any(keyword => errorText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        errorFound = true;
                                        _logger.LogInformation("[Playwright] Q1: Invalid credentials test PASS - error message found using selector: {Selector}", selector);
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }

                        // If not found via selectors, check page body text
                        if (!errorFound)
                        {
                            try
                            {
                                var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 }) ?? string.Empty;
                                if (errorKeywords.Any(keyword => bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                                {
                                    errorFound = true;
                                    _logger.LogInformation("[Playwright] Q1: Invalid credentials test PASS - error keyword found in body text");
                                }
                            }
                            catch
                            {
                                // Ignore if can't read body
                            }
                        }

                        if (!errorFound)
                        {
                            score -= 0.25;
                            failedTestCases.Add("Invalid Credentials");
                            _logger.LogWarning("[Playwright] Q1: Invalid credentials error message not found. Deducted 0.25");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Q1: Invalid credentials test encountered an error: {Message}. Score set to 0.", ex.Message);
                    return 0;
                }

                // Step 3: Test admin credentials (admin@Panther.com/@1) - should show "You have no permission to access this function!"
                try
                {
                    // Navigate back to saved login page URL if available
                    if (!string.IsNullOrWhiteSpace(_loginPageUrl))
                    {
                        _logger.LogInformation("[Playwright] Q1: Navigating to saved login page URL before test case 2 (Admin): {Url}", _loginPageUrl);
                        try
                        {
                            await page.GotoAsync(_loginPageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                            await page.WaitForTimeoutAsync(1000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[Playwright] Q1: Failed to navigate to saved login URL: {Message}, trying fallback", ex.Message);
                            var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                            if (!navigated)
                            {
                                _logger.LogWarning("[Playwright] Q1: Unable to return to login page before admin credential test. Score set to 0.");
                                return 0;
                            }
                        }
                    }
                    else
                    {
                        // Fallback to original method if login URL not saved
                        var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                        if (!navigated)
                        {
                            _logger.LogWarning("[Playwright] Q1: Unable to return to login page before admin credential test. Score set to 0.");
                            return 0;
                        }
                    }
                    
                    // Wait for page to be ready and verify we're on login page
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                    }
                    catch
                    {
                        // Continue if network idle timeout
                    }
                    await page.WaitForTimeoutAsync(1500);

                    // Retry finding login elements with multiple attempts
                    emailInput = null;
                    passwordInput = null;
                    var loginButton = (ILocator?)null;
                    
                    for (int retry = 0; retry < 3; retry++)
                    {
                        if (retry > 0)
                        {
                            await page.WaitForTimeoutAsync(1000);
                            _logger.LogDebug("[Playwright] Q1: Retry {Retry}/3 finding login elements", retry + 1);
                        }
                        
                        emailInput = await finder.FindEmailInputAsync();
                        passwordInput = await finder.FindPasswordInputAsync();
                        loginButton = await finder.FindLoginButtonAsync();
                        
                        if (emailInput != null && passwordInput != null && loginButton != null)
                        {
                            break;
                        }
                    }

                    if (emailInput == null || passwordInput == null || loginButton == null)
                    {
                        _logger.LogWarning("[Playwright] Q1: Login form elements missing during admin credential test after retries. Score set to 0.");
                        return 0;
                    }
                    else
                    {
                        await emailInput.ClearAsync();
                        await emailInput.FillAsync(adminCredentials.Email ?? string.Empty);
                        await passwordInput.ClearAsync();
                        await passwordInput.FillAsync(adminCredentials.Password ?? string.Empty);
                        await loginButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        await page.WaitForTimeoutAsync(1500); // Wait for response and potential redirect

                        // Check for permission error message with multiple strategies
                        var permissionStrategies = new List<string>
                        {
                            "text='You have no permission to access this function!'",
                            "text*='no permission'",
                            "text*='No permission'",
                            "text*='permission'",
                            "text*='Permission'",
                            "[class*='error']",
                            "[class*='Error']",
                            "[role='alert']",
                            "div:has-text('permission')",
                            "div:has-text('Permission')",
                            "span:has-text('permission')",
                            "p:has-text('permission')"
                        };

                        foreach (var keyword in permissionKeywords)
                        {
                            if (!string.IsNullOrWhiteSpace(keyword))
                            {
                                permissionStrategies.Add($"text*='{keyword}'");
                            }
                        }

                        bool permissionErrorFound = false;
                        foreach (var strategy in permissionStrategies)
                        {
                            try
                            {
                                var isVisible = await page.Locator(strategy).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 });
                                if (isVisible)
                                {
                                    permissionErrorFound = true;
                                    _logger.LogInformation("[Playwright] Q1: Admin permission test PASS - error message found using: {Strategy}", strategy);
                                    break;
                                }
                            }
                            catch { continue; }
                        }

                        // Also check page body text as fallback
                        if (!permissionErrorFound)
                        {
                            try
                            {
                                var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 }) ?? string.Empty;
                                if (permissionKeywords.Any(keyword => bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                                {
                                    permissionErrorFound = true;
                                    _logger.LogInformation("[Playwright] Q1: Admin permission test PASS - permission text found in body");
                                }
                            }
                            catch
                            {
                                // Ignore if can't read body
                            }
                        }

                        if (!permissionErrorFound)
                        {
                            score -= 0.25;
                            failedTestCases.Add("Permission Denied");
                            _logger.LogWarning("[Playwright] Q1: Permission error message not found. Deducted 0.25");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Q1: Admin permission test encountered an error: {Message}. Score set to 0.", ex.Message);
                    return 0;
                }

                // Step 4: Test manager credentials (manager@Panther.com/@1) - should redirect to panther list
                try
                {
                    // Navigate back to saved login page URL if available
                    if (!string.IsNullOrWhiteSpace(_loginPageUrl))
                    {
                        _logger.LogInformation("[Playwright] Q1: Navigating to saved login page URL before test case 3 (Manager): {Url}", _loginPageUrl);
                        try
                        {
                            await page.GotoAsync(_loginPageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                            await page.WaitForTimeoutAsync(1000);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("[Playwright] Q1: Failed to navigate to saved login URL: {Message}, trying fallback", ex.Message);
                            var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                            if (!navigated)
                            {
                                _logger.LogWarning("[Playwright] Q1: Unable to return to login page before manager credential test. Score set to 0.");
                                return 0;
                            }
                        }
                    }
                    else
                    {
                        // Fallback to original method if login URL not saved
                        var navigated = await NavigateToLoginPageOrLogoutAsync(page, loginUrl, finder);
                        if (!navigated)
                        {
                            _logger.LogWarning("[Playwright] Q1: Unable to return to login page before manager credential test. Score set to 0.");
                            return 0;
                        }
                    }
                    
                    // Wait for page to be ready and verify we're on login page
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                    }
                    catch
                    {
                        // Continue if network idle timeout
                    }
                    await page.WaitForTimeoutAsync(1500);

                    // Retry finding login elements with multiple attempts
                    emailInput = null;
                    passwordInput = null;
                    var loginButton = (ILocator?)null;
                    
                    for (int retry = 0; retry < 3; retry++)
                    {
                        if (retry > 0)
                        {
                            await page.WaitForTimeoutAsync(1000);
                            _logger.LogDebug("[Playwright] Q1: Retry {Retry}/3 finding login elements for manager test", retry + 1);
                        }
                        
                        emailInput = await finder.FindEmailInputAsync();
                        passwordInput = await finder.FindPasswordInputAsync();
                        loginButton = await finder.FindLoginButtonAsync();
                        
                        if (emailInput != null && passwordInput != null && loginButton != null)
                        {
                            break;
                        }
                    }

                    if (emailInput == null || passwordInput == null || loginButton == null)
                    {
                        _logger.LogWarning("[Playwright] Q1: Login form elements missing during manager credential test after retries. Score set to 0.");
                        return 0;
                    }
                    else
                    {
                        await emailInput.ClearAsync();
                        await emailInput.FillAsync(managerCredentials.Email ?? string.Empty);
                        await passwordInput.ClearAsync();
                        await passwordInput.FillAsync(managerCredentials.Password ?? string.Empty);
                        await loginButton.ClickAsync();
                        await page.WaitForTimeoutAsync(2000); // Wait for redirect
                        try
                        {
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                        }
                        catch
                        {
                            // Continue if network idle timeout
                        }

                        // Check if navigation to panther list page was successful
                        // Only check if we're on list page (URL contains PantherProfile/PantherProfiles), no need to check columns
                        var isOnListPage = IsListPageUrl(page.Url);
                        if (!isOnListPage)
                        {
                            // Also check if table exists as fallback (some pages might not have standard URL pattern)
                            var hasTable = await page.Locator("table").IsVisibleAsync();
                            if (!hasTable)
                            {
                                score -= 0.5;
                                failedTestCases.Add("Manager Login");
                                _logger.LogWarning("[Playwright] Q1: Manager login did not navigate to list page. Deducted 0.5");
                            }
                            else
                            {
                                // Save the list page URL even if URL pattern doesn't match but table exists
                                _listPageUrl = page.Url;
                                _logger.LogInformation("[Playwright] Q1: Manager login test PASS - table found (navigation successful). Saved list page URL: {Url}", _listPageUrl);
                            }
                        }
                        else
                        {
                            // Save the list page URL for use in later tests (especially Q4 Update OK)
                            _listPageUrl = page.Url;
                            _logger.LogInformation("[Playwright] Q1: Manager login test PASS - successfully navigated to panther list page. Saved list page URL: {Url}", _listPageUrl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Q1: Manager login test encountered an error: {Message}. Score set to 0.", ex.Message);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q1: Critical error in login test: {Message}", ex.Message);
                return 0;
            }

            // Populate TestResultDetail with note
            if (_testResultDetail != null)
            {
                _testResultDetail.Q1Login = score;
                if (failedTestCases.Count > 0)
                {
                    _testResultDetail.Q1LoginNote = $"Fail test case{((failedTestCases.Count > 1) ? "s" : "")}: {string.Join(", ", failedTestCases)}";
                }
            }

            return Math.Max(0, score); // Ensure score doesn't go below 0
        }

        private async Task<double> RunQ2PantherListTest(IPage page)
        {
            double score = 0.0; // Start with 0, add points for each test
            double listAllScore = 0.0;
            string listAllNote = string.Empty;
            double listAll2Score = 0.0;
            string listAll2Note = string.Empty;
            double paggingScore = 0.0;
            string paggingNote = string.Empty;
            var listSettings = _settings.PantherList ?? new PantherListTestSettings();
            var typeColumnNames = (listSettings.TypeColumnCandidates?.Count > 0
                ? listSettings.TypeColumnCandidates
                : new List<string> { "PantherType", "TypeName", "Type" });
            var requiredColumns = listSettings.RequiredColumns?.Count > 0
                ? listSettings.RequiredColumns
                : new Dictionary<string, List<string>>
                {
                    { "PantherName", new List<string> { "PantherName", "panthername", "Name", "name" } },
                    { "Weight", new List<string> { "Weight", "weight" } },
                    { "Characteristics", new List<string> { "Characteristics", "characteristics" } },
                    { "Warning", new List<string> { "Warning", "warning" } },
                    { "ModifiedDate", new List<string> { "ModifiedDate", "modifieddate", "Modified", "modified", "Date", "date" } }
                };
            var expectedFirstPantherName = string.IsNullOrWhiteSpace(listSettings.ExpectedFirstPantherName)
                ? "Jaguars leopards"
                : listSettings.ExpectedFirstPantherName;

            try
            {
                // Wait for page to be ready (should already be on panther list page after Q1)
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                }
                catch
                {
                    // Continue if network idle timeout
                }
                await page.WaitForTimeoutAsync(1000);

                // Part 1: ListAll (0.25 points) - Check Type column (PantherType/TypeName/Type) must be text not number
                try
                {
                    var typeColumnIsText = false;
                    string? foundTypeColumnName = null;

                    // Check if table exists
                    var hasTable = await page.Locator("table").IsVisibleAsync();
                    if (!hasTable)
                    {
                        _logger.LogWarning("[Playwright] Q2 ListAll: Table not found");
                    }
                    else
                    {
                        var allHeaders = await page.Locator("table th").AllTextContentsAsync();

                        // Check if any type column exists (PantherType, TypeName, or Type)
                        bool typeColumnFound = false;
                        for (int i = 0; i < allHeaders.Count; i++)
                        {
                            foreach (var typeName in typeColumnNames)
                            {
                                if (allHeaders[i].Contains(typeName, StringComparison.OrdinalIgnoreCase))
                                {
                                    typeColumnFound = true;
                                    foundTypeColumnName = typeName;
                                    _logger.LogInformation("[Playwright] Q2 ListAll: Found type column: '{TypeColumn}'", typeName);
                                    break;
                                }
                            }
                            if (typeColumnFound) break;
                        }

                        if (!typeColumnFound)
                        {
                            _logger.LogWarning("[Playwright] Q2 ListAll: No type column found (checked: PantherType, TypeName, Type)");
                        }
                        else
                        {
                            // Find the type column index
                            var headers = await page.Locator("table th").AllTextContentsAsync();
                            var typeColumnIndex = -1;
                            for (int i = 0; i < headers.Count; i++)
                            {
                                if (foundTypeColumnName != null && headers[i].Contains(foundTypeColumnName, StringComparison.OrdinalIgnoreCase))
                                {
                                    typeColumnIndex = i;
                                    break;
                                }
                            }

                            if (typeColumnIndex >= 0)
                            {
                                // Get first row of type column to check if data is text
                                var rows = await page.Locator("table tbody tr").AllAsync();
                                if (rows.Count > 0)
                                {
                                    var firstRowCells = await rows[0].Locator("td").AllAsync();
                                    if (firstRowCells.Count > typeColumnIndex)
                                    {
                                        var typeValue = await firstRowCells[typeColumnIndex].TextContentAsync();
                                        if (!string.IsNullOrEmpty(typeValue))
                                        {
                                            // Check if it's not a pure number (must be text)
                                            var trimmed = typeValue.Trim();
                                            typeColumnIsText = !int.TryParse(trimmed, out _) && !double.TryParse(trimmed, out _);
                                            
                                            if (!typeColumnIsText)
                                            {
                                                _logger.LogWarning("[Playwright] Q2 ListAll: Type column '{TypeColumn}' has number value '{Value}', must be text", foundTypeColumnName, trimmed);
                                            }
                                            else
                                            {
                                                _logger.LogInformation("[Playwright] Q2 ListAll: Type column '{TypeColumn}' has text value '{Value}'", foundTypeColumnName, trimmed);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (typeColumnIsText)
                    {
                        listAllScore = 0.25;
                        score += 0.25;
                        _logger.LogInformation("[Playwright] Q2 ListAll PASS: Type column '{TypeColumn}' is text", foundTypeColumnName ?? "unknown");
                    }
                    else
                    {
                        listAllNote = "Fail test case: Type column must be text, not number";
                        _logger.LogWarning("[Playwright] Q2 ListAll FAIL: Type column is text: {IsText}", typeColumnIsText);
                    }
                }
                catch (Exception ex)
                {
                    listAllNote = $"Fail test case: {ex.Message}";
                    _logger.LogWarning("[Playwright] Q2 ListAll failed: {Message}", ex.Message);
                }

                // Part 2: ListAll2 (0.25 points) - Check required columns exist and first PantherName matches expected value
                try
                {
                    var hasTable = await page.Locator("table").IsVisibleAsync();
                    if (!hasTable)
                    {
                        _logger.LogWarning("[Playwright] Q2 ListAll2: Table not found");
                    }
                    else
                    {
                        var headers = await page.Locator("table th").AllTextContentsAsync();
                        var headerList = headers.ToList();
                        
                        // Check required columns with flexible names
                        bool allColumnsFound = true;
                        foreach (var column in requiredColumns)
                        {
                            var aliases = column.Value?.ToArray() ?? Array.Empty<string>();
                            var found = aliases.Length > 0
                                ? FindColumnIndex(headerList, aliases) >= 0
                                : false;
                            if (!found)
                            {
                                allColumnsFound = false;
                                _logger.LogWarning("[Playwright] Q2 ListAll2: Column '{Column}' not found (checked variants: {Variants})", 
                                    column.Key, string.Join(", ", aliases));
                            }
                        }

                        if (!allColumnsFound)
                        {
                            _logger.LogWarning("[Playwright] Q2 ListAll2: Not all required columns found");
                        }
                        else
                        {
                            // Find PantherName column index (flexible)
                            var pantherNameIndex = FindColumnIndex(headerList, "PantherName", "panthername", "Name", "name");

                            if (pantherNameIndex >= 0)
                            {
                                // Get first row's PantherName
                                var rows = await page.Locator("table tbody tr").AllAsync();
                                if (rows.Count > 0)
                                {
                                    var firstRowCells = await rows[0].Locator("td").AllAsync();
                                    if (firstRowCells.Count > pantherNameIndex)
                                    {
                                        var firstPantherName = await firstRowCells[pantherNameIndex].TextContentAsync();
                                        if (!string.IsNullOrEmpty(firstPantherName))
                                        {
                                            var trimmed = firstPantherName.Trim();
                                            if (trimmed.Equals(expectedFirstPantherName, StringComparison.OrdinalIgnoreCase))
                                            {
                                                listAll2Score = 0.25;
                                                score += 0.25;
                                                _logger.LogInformation("[Playwright] Q2 ListAll2 PASS: All required columns found and first PantherName is '{Expected}'", expectedFirstPantherName);
                                            }
                                            else
                                            {
                                                listAll2Note = $"Fail test case: First PantherName is '{trimmed}', expected '{expectedFirstPantherName}'";
                                                _logger.LogWarning("[Playwright] Q2 ListAll2 FAIL: First PantherName is '{Value}', expected '{Expected}'", trimmed, expectedFirstPantherName);
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("[Playwright] Q2 ListAll2: PantherName column not found");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    listAll2Note = $"Fail test case: {ex.Message}";
                    _logger.LogWarning("[Playwright] Q2 ListAll2 failed: {Message}", ex.Message);
                }

                // Continue to pagination checks without interaction

                // Part 3: Pagging (1.0 points) - Check pagination exists (no click required)
                try
                {
                    // Presence-only check for pagination controls
                    var paginationExists = await page.Locator("[class*='pagination'], [class*='paging']").IsVisibleAsync() ||
                                          await page.Locator("nav[aria-label*='pagination' i]").IsVisibleAsync() ||
                                          await page.Locator("a.page-link, button.page-link").IsVisibleAsync() ||
                                          await page.Locator("a:has-text('2'), button:has-text('2'), span:has-text('2')").IsVisibleAsync();

                    if (paginationExists)
                    {
                        paggingScore = 1.0;
                        score += 1.0;
                        _logger.LogInformation("[Playwright] Q2 Pagging PASS: Pagination control detected (no click)");
                    }
                    else
                    {
                        paggingNote = "Fail test case: Pagination control not found";
                        _logger.LogWarning("[Playwright] Q2 Pagging: Pagination not found");
                    }
                }
                catch (Exception ex)
                {
                    paggingNote = $"Fail test case: {ex.Message}";
                    _logger.LogWarning("[Playwright] Q2 Pagging failed: {Message}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q2: Critical error in panther list test: {Message}", ex.Message);
            }

            // Populate TestResultDetail
            if (_testResultDetail != null)
            {
                _testResultDetail.Q2ListAll = listAllScore;
                _testResultDetail.Q2ListAllNote = listAllNote;
                _testResultDetail.Q2ListAll2 = listAll2Score;
                _testResultDetail.Q2ListAll2Note = listAll2Note;
                _testResultDetail.Q2Pagging = paggingScore;
                _testResultDetail.Q2PaggingNote = paggingNote;
            }

            return Math.Min(1.5, score); // Ensure score doesn't exceed 1.5
        }

        private async Task<double> RunQ3PantherCreateTest(IPage page, string canonicalBaseUrl)
        {
            // Reset Q3 Display Top result at the start of Q3 test
            _q3DisplayTopPassed = false;
            double score = 0.0;
            var createSettings = _settings.PantherCreate ?? new PantherCreateTestSettings();
            var expectedOptions = createSettings.ExpectedPantherTypeOptions?.Count > 0
                ? createSettings.ExpectedPantherTypeOptions
                : new List<string> { "Black leopards", "Black jaguars", "Pumas" };
            var validationLengthData = createSettings.ValidationLength ?? new PantherFormData();
            var validationSpecialCharsData = createSettings.ValidationSpecialCharacters ?? new PantherFormData();
            var validationRequiredData = createSettings.ValidationRequired ?? new PantherFormData();
            var addOkData = createSettings.AddOk ?? new PantherFormData();
            var displayTopName = string.IsNullOrWhiteSpace(createSettings.DisplayTopPantherName)
                ? addOkData.PantherName
                : createSettings.DisplayTopPantherName;
            
            // Track scores and notes for each test case
            double addOkScore = 0.0;
            string addOkNote = string.Empty;
            double displayTopScore = 0.0;
            string displayTopNote = string.Empty;
            double validationComboboxScore = 0.0;
            string validationComboboxNote = string.Empty;
            double validationRequiredScore = 0.0;
            string validationRequiredNote = string.Empty;
            double validationLengthScore = 0.0;
            string validationLengthNote = string.Empty;
            double validationSpecialCharactersScore = 0.0;
            string validationSpecialCharactersNote = string.Empty;

            try
            {
                // Ensure we start on the create page; if this fails, the entire Q3 is zero.
                if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q3: Unable to navigate to PantherProfile create page. Score set to 0.");
                    return 0;
                }

                // Validation Combobox (0.25)
                var (comboExists, optionTexts) = await GetPantherTypeOptionsAsync(page);

                if (comboExists && optionTexts.Count > 0)
                {
                    var hasAllOptions = expectedOptions.All(option =>
                        optionTexts.Any(text => string.Equals(text, option, StringComparison.OrdinalIgnoreCase)));

                    if (hasAllOptions)
                    {
                        validationComboboxScore = 0.25;
                        score += 0.25;
                        _logger.LogInformation("[Playwright] Q3 Combobox PASS: Expected PantherType options found.");
                    }
                    else
                    {
                        validationComboboxNote = $"Fail test case: Missing expected PantherType options. Found {string.Join(", ", optionTexts)}";
                        _logger.LogWarning("[Playwright] Q3 Combobox FAIL: Missing expected PantherType options. Found {Options}", string.Join(", ", optionTexts));
                    }
                }
                else
                {
                    validationComboboxNote = "Fail test case: PantherType select/combobox not detected";
                    _logger.LogWarning("[Playwright] Q3 Combobox FAIL: PantherType select/combobox not detected.");
                }

                // Validation - characters length (0.25)
                var date1 = FormatDateForInput(DateTime.UtcNow);
                if (!await SubmitCreateFormAsync(
                        page,
                        canonicalBaseUrl,
                        validationLengthData.PantherTypeIndex,
                        validationLengthData.PantherName,
                        validationLengthData.Weight,
                        validationLengthData.Characteristics,
                        validationLengthData.Warning,
                        date1))
                {
                    _logger.LogWarning("[Playwright] Q3 Validation Length FAIL: Create action resulted in error. Score set to 0.");
                    return 0;
                }
                await page.WaitForTimeoutAsync(1000);
                
                // Check if still on create page (validation should prevent navigation)
                var isStillOnCreatePage = IsCreatePageUrl(page.Url);
                var navigatedToList = IsListPageUrl(page.Url);
                var noFatalError = await CheckForFatalPageStateAsync(page);
                
                if (isStillOnCreatePage && !navigatedToList && noFatalError)
                {
                    validationLengthScore = 0.25;
                    score += 0.25;
                    _logger.LogInformation("[Playwright] Q3 Validation Length PASS: Still on create page after submit (validation prevented navigation).");
                }
                else
                {
                    validationLengthNote = "Fail test case: Validation did not prevent navigation";
                    _logger.LogWarning("[Playwright] Q3 Validation Length FAIL: Navigated away from create page (isStillOnCreate={StillOnCreate}, navigatedToList={NavigatedToList}, noFatalError={NoFatalError})", 
                        isStillOnCreatePage, navigatedToList, noFatalError);
                }
                
                // Navigate back to create page for next test
                if (!isStillOnCreatePage)
                {
                    if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q3: Unable to return to create page after short-name test. Score set to 0.");
                        return 0;
                    }
                }

                // Validation - No special characters (0.5)
                var date2 = FormatDateForInput(DateTime.UtcNow.AddMinutes(1));
                if (!await SubmitCreateFormAsync(
                        page,
                        canonicalBaseUrl,
                        validationSpecialCharsData.PantherTypeIndex,
                        validationSpecialCharsData.PantherName,
                        validationSpecialCharsData.Weight,
                        validationSpecialCharsData.Characteristics,
                        validationSpecialCharsData.Warning,
                        date2))
                {
                    _logger.LogWarning("[Playwright] Q3 Validation Special Characters FAIL: Create action resulted in error. Score set to 0.");
                    return 0;
                }
                await page.WaitForTimeoutAsync(1000);
                
                // Check if still on create page (validation should prevent navigation)
                var isStillOnCreatePage2 = IsCreatePageUrl(page.Url);
                var navigatedToList2 = IsListPageUrl(page.Url);
                var noFatalError2 = await CheckForFatalPageStateAsync(page);
                
                if (isStillOnCreatePage2 && !navigatedToList2 && noFatalError2)
                {
                    validationSpecialCharactersScore = 0.5;
                    score += 0.5;
                    _logger.LogInformation("[Playwright] Q3 Validation Special Characters PASS: Still on create page after submit (validation prevented navigation).");
                }
                else
                {
                    validationSpecialCharactersNote = "Fail test case: Validation did not prevent navigation";
                    _logger.LogWarning("[Playwright] Q3 Validation Special Characters FAIL: Navigated away from create page (isStillOnCreate={StillOnCreate}, navigatedToList={NavigatedToList}, noFatalError={NoFatalError})", 
                        isStillOnCreatePage2, navigatedToList2, noFatalError2);
                }
                
                // Navigate back to create page for next test
                if (!isStillOnCreatePage2)
                {
                    if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q3: Unable to return to create page after special-character test. Score set to 0.");
                        return 0;
                    }
                }

                // Validation Required (0.25) - Weight left empty
                var date3 = FormatDateForInput(DateTime.UtcNow.AddMinutes(2));
                if (!await SubmitCreateFormAsync(
                        page,
                        canonicalBaseUrl,
                        validationRequiredData.PantherTypeIndex,
                        validationRequiredData.PantherName,
                        validationRequiredData.Weight,
                        validationRequiredData.Characteristics,
                        validationRequiredData.Warning,
                        date3))
                {
                    _logger.LogWarning("[Playwright] Q3 Validation Required FAIL: Create action resulted in error. Score set to 0.");
                    return 0;
                }
                await page.WaitForTimeoutAsync(1000);
                
                // Check if still on create page (validation should prevent navigation)
                var isStillOnCreatePage3 = IsCreatePageUrl(page.Url);
                var navigatedToList3 = IsListPageUrl(page.Url);
                var noFatalError3 = await CheckForFatalPageStateAsync(page);
                
                if (isStillOnCreatePage3 && !navigatedToList3 && noFatalError3)
                {
                    validationRequiredScore = 0.25;
                    score += 0.25;
                    _logger.LogInformation("[Playwright] Q3 Validation Required PASS: Still on create page after submit (validation prevented navigation).");
                }
                else
                {
                    validationRequiredNote = "Fail test case: Validation did not prevent navigation";
                    _logger.LogWarning("[Playwright] Q3 Validation Required FAIL: Navigated away from create page (isStillOnCreate={StillOnCreate}, navigatedToList={NavigatedToList}, noFatalError={NoFatalError})", 
                        isStillOnCreatePage3, navigatedToList3, noFatalError3);
                }
                
                // Navigate back to create page for next test
                if (!isStillOnCreatePage3)
                {
                    if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q3: Unable to return to create page after required-field test. Score set to 0.");
                        return 0;
                    }
                }

                // Add OK (1.0)
                if (!IsCreatePageUrl(page.Url))
                {
                if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q3: Unable to reach create page before final submission. Score set to 0.");
                    return 0;
                }
                }

                var date4 = FormatDateForInput(DateTime.UtcNow.AddMinutes(3));
                if (!await SubmitCreateFormAsync(
                        page,
                        canonicalBaseUrl,
                        addOkData.PantherTypeIndex,
                        addOkData.PantherName,
                        addOkData.Weight,
                        addOkData.Characteristics,
                        addOkData.Warning,
                        date4))
                {
                    _logger.LogWarning("[Playwright] Q3 Add OK FAIL: Create action resulted in error. Score set to 0.");
                    return 0;
                }
                await page.WaitForTimeoutAsync(600);
                var navigatedAfterValidCreate = await WaitForListNavigationAsync(page);
                if (navigatedAfterValidCreate)
                {
                    addOkScore = 1.0;
                    score += 1.0;
                    _logger.LogInformation("[Playwright] Q3 Add OK PASS: Valid PantherProfile created successfully.");
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                    }
                    catch
                    {
                        // ignore timeout
                    }
                }
                else
                {
                    addOkNote = "Fail test case: Valid PantherProfile did not navigate to list";
                    _logger.LogWarning("[Playwright] Q3 Add OK FAIL: Valid PantherProfile did not navigate to list.");
                }

                // Display Top (0.25)
                if (IsListPageUrl(page.Url))
                {
                    var isFirst = await IsNewPantherFirstAsync(page, displayTopName);
                    if (isFirst)
                    {
                        displayTopScore = 0.25;
                        score += 0.25;
                        _q3DisplayTopPassed = true; // Store result for Q5 adjustment
                        _logger.LogInformation("[Playwright] Q3 Display Top PASS: Newly created Panther appears first.");
                    }
                    else
                    {
                        displayTopNote = "Fail test case: Newly created Panther not found at top of list";
                        _q3DisplayTopPassed = false; // Store result for Q5 adjustment
                        _logger.LogWarning("[Playwright] Q3 Display Top FAIL: Newly created Panther not found at top of list.");
                    }
                }
                else
                {
                    displayTopNote = "Fail test case: Not on PantherProfile list page after create";
                    _q3DisplayTopPassed = false; // Store result for Q5 adjustment (skip = fail)
                    _logger.LogWarning("[Playwright] Q3 Display Top SKIP: Not on PantherProfile list page after create.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q3: Critical error during create workflow test: {Message}", ex.Message);
            }

            // Populate TestResultDetail
            if (_testResultDetail != null)
            {
                _testResultDetail.Q3AddOk = addOkScore;
                _testResultDetail.Q3AddOkNote = addOkNote;
                _testResultDetail.Q3DisplayTop = displayTopScore;
                _testResultDetail.Q3DisplayTopNote = displayTopNote;
                _testResultDetail.Q3ValidationCombobox = validationComboboxScore;
                _testResultDetail.Q3ValidationComboboxNote = validationComboboxNote;
                _testResultDetail.Q3ValidationRequired = validationRequiredScore;
                _testResultDetail.Q3ValidationRequiredNote = validationRequiredNote;
                _testResultDetail.Q3ValidationLength = validationLengthScore;
                _testResultDetail.Q3ValidationLengthNote = validationLengthNote;
                _testResultDetail.Q3ValidationSpecialCharacters = validationSpecialCharactersScore;
                _testResultDetail.Q3ValidationSpecialCharactersNote = validationSpecialCharactersNote;
            }

            return Math.Min(2.5, Math.Max(0, score));
        }

        private async Task<double> RunQ4PantherUpdateTest(IPage page, string canonicalBaseUrl)
        {
            double score = 0.0;
            double updateOkScore = 0.0;
            string updateOkNote = string.Empty;
            double updateValidationScore = 0.0;
            string updateValidationNote = string.Empty;
            var updateSettings = _settings.PantherUpdate ?? new PantherUpdateTestSettings();

            try
            {
                if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q4: Unable to reach panther list before running update tests. Score set to 0.");
                    return 0;
                }

                if (!await NavigateToEditPageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q4: Unable to open edit page. Score set to 0.");
                    return 0;
                }

                var (validationScore, validationNote) = await RunQ4UpdateValidationTestsAsync(page, canonicalBaseUrl, updateSettings);
                updateValidationScore = validationScore;
                updateValidationNote = validationNote;
                score += validationScore;
                _logger.LogInformation("[Playwright] Q4 Validation Score: {Score}/1.0", validationScore);

                if (!await EnsureEditPageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q4: Unable to return to edit page before Update OK test.");
                    // Populate TestResultDetail before returning
                    if (_testResultDetail != null)
                    {
                        _testResultDetail.Q4UpdateOk = updateOkScore;
                        _testResultDetail.Q4UpdateOkNote = updateOkNote;
                        _testResultDetail.Q4UpdateValidation = updateValidationScore;
                        _testResultDetail.Q4UpdateValidationNote = updateValidationNote;
                    }
                    return Math.Max(0, score);
                }

                var updateOk = await RunQ4UpdateOkTestAsync(page, canonicalBaseUrl, updateSettings);
                if (updateOk)
                {
                    updateOkScore = 1.0;
                    score += 1.0;
                    _logger.LogInformation("[Playwright] Q4 Update OK PASS: Panther updated successfully.");
                }
                else
                {
                    updateOkNote = "Fail test case: Panther update verification failed";
                    _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Panther update verification failed.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q4: Critical error during update workflow test: {Message}", ex.Message);
            }

            // Populate TestResultDetail
            if (_testResultDetail != null)
            {
                _testResultDetail.Q4UpdateOk = updateOkScore;
                _testResultDetail.Q4UpdateOkNote = updateOkNote;
                _testResultDetail.Q4UpdateValidation = updateValidationScore;
                _testResultDetail.Q4UpdateValidationNote = updateValidationNote;
            }

            return Math.Min(2.0, Math.Max(0, score));
        }

        private async Task<(double Score, string Note)> RunQ4UpdateValidationTestsAsync(IPage page, string canonicalBaseUrl, PantherUpdateTestSettings updateSettings)
        {
            double score = 1.0;
            var expectedOptions = updateSettings.ExpectedPantherTypeOptions?.Count > 0
                ? updateSettings.ExpectedPantherTypeOptions
                : new List<string> { "Black leopards", "Black jaguars", "Pumas" };
            var failedValidations = new List<string>();
            var validationLengthData = updateSettings.ValidationLength ?? new PantherFormData();
            var validationSpecialCharsData = updateSettings.ValidationSpecialCharacters ?? new PantherFormData();
            var validationRequiredData = updateSettings.ValidationRequiredWeight ?? new PantherFormData();

            if (!await EnsureEditPageAsync(page, canonicalBaseUrl))
            {
                _logger.LogWarning("[Playwright] Q4 Validation: Edit page not available.");
                return (0, "Fail test case: Edit page not available");
            }

            var (comboExists, optionTexts) = await GetPantherTypeOptionsAsync(page);
            if (comboExists && expectedOptions.All(option => optionTexts.Any(text => string.Equals(text, option, StringComparison.OrdinalIgnoreCase))))
            {
                _logger.LogInformation("[Playwright] Q4 Validation: PantherType combobox options verified.");
            }
            else
            {
                score -= 0.25;
                failedValidations.Add("Combobox");
                _logger.LogWarning("[Playwright] Q4 Validation FAIL: PantherType combobox missing expected options. Found {Options}", string.Join(", ", optionTexts));
            }

            async Task<bool> RunValidationCaseAsync(
                string description,
                Func<Task<bool>> submitFunc,
                IEnumerable<string> validationMessages)
            {
                if (!await submitFunc())
                {
                    score -= 0.25;
                    failedValidations.Add(description);
                    _logger.LogWarning("[Playwright] Q4 Validation {Description} FAIL: Submission error prevented validation.", description);
                    return await EnsureEditPageAsync(page, canonicalBaseUrl);
                }

                // Wait for page to settle after submit
                await page.WaitForTimeoutAsync(1000);
                
                // Check if still on edit page (validation should prevent navigation)
                var isStillOnEditPage = IsEditPageUrl(page.Url);
                var navigatedToList = IsListPageUrl(page.Url);
                var noFatalError = await CheckForFatalPageStateAsync(page);
                
                if (isStillOnEditPage && !navigatedToList && noFatalError)
                {
                    _logger.LogInformation("[Playwright] Q4 Validation {Description} PASS: Still on edit page after submit (validation prevented navigation).", description);
                    // Still on edit page, no need to navigate back
                    return true;
                }
                else
                {
                    score -= 0.25;
                    failedValidations.Add(description);
                    _logger.LogWarning("[Playwright] Q4 Validation {Description} FAIL: Navigated away from edit page (isStillOnEdit={StillOnEdit}, navigatedToList={NavigatedToList}, noFatalError={NoFatalError})", 
                        description, isStillOnEditPage, navigatedToList, noFatalError);
                    
                    // Navigate back to edit page for next test
                    return await EnsureEditPageAsync(page, canonicalBaseUrl);
                }
            }

            var dateBase = DateTime.UtcNow;

            if (!await RunValidationCaseAsync(
                "Length",
                () => SubmitUpdateFormAsync(
                    page,
                    canonicalBaseUrl,
                    validationLengthData.PantherTypeIndex,
                    validationLengthData.PantherName,
                    validationLengthData.Weight,
                    validationLengthData.Characteristics,
                    validationLengthData.Warning,
                    FormatDateForInput(dateBase.AddMinutes(4))),
                new[]
                {
                    "Each word must start with a capital letter",
                    "Each word must start with a capital letter, can include letters or numbers, no special characters"
                }))
            {
                var note = failedValidations.Count > 0 
                    ? $"Fail test case{((failedValidations.Count > 1) ? "s" : "")}: {string.Join(", ", failedValidations)}"
                    : string.Empty;
                return (Math.Max(0, Math.Min(1.0, score)), note);
            }

            if (!await RunValidationCaseAsync(
                "Special Characters",
                () => SubmitUpdateFormAsync(
                    page,
                    canonicalBaseUrl,
                    validationSpecialCharsData.PantherTypeIndex,
                    validationSpecialCharsData.PantherName,
                    validationSpecialCharsData.Weight,
                    validationSpecialCharsData.Characteristics,
                    validationSpecialCharsData.Warning,
                    FormatDateForInput(dateBase.AddMinutes(5))),
                new[]
                {
                    "Each word must start with a capital letter",
                    "no special characters"
                }))
            {
                var note = failedValidations.Count > 0 
                    ? $"Fail test case{((failedValidations.Count > 1) ? "s" : "")}: {string.Join(", ", failedValidations)}"
                    : string.Empty;
                return (Math.Max(0, Math.Min(1.0, score)), note);
            }

            if (!await RunValidationCaseAsync(
                "Required Weight",
                () => SubmitUpdateFormAsync(
                    page,
                    canonicalBaseUrl,
                    validationRequiredData.PantherTypeIndex,
                    validationRequiredData.PantherName,
                    validationRequiredData.Weight,
                    validationRequiredData.Characteristics,
                    validationRequiredData.Warning,
                    FormatDateForInput(dateBase.AddMinutes(6))),
                new[]
                {
                    "The Weight field is required"
                }))
            {
                var note = failedValidations.Count > 0 
                    ? $"Fail test case{((failedValidations.Count > 1) ? "s" : "")}: {string.Join(", ", failedValidations)}"
                    : string.Empty;
                return (Math.Max(0, Math.Min(1.0, score)), note);
            }

            var finalNote = failedValidations.Count > 0 
                ? $"Fail test case{((failedValidations.Count > 1) ? "s" : "")}: {string.Join(", ", failedValidations)}"
                : string.Empty;
            return (Math.Max(0, Math.Min(1.0, score)), finalNote);
        }

        private async Task<bool> RunQ4UpdateOkTestAsync(IPage page, string canonicalBaseUrl, PantherUpdateTestSettings updateSettings)
        {
            var date = FormatDateForInput(DateTime.UtcNow.AddMinutes(7));
            var updateOkData = updateSettings.UpdateOk ?? new PantherFormData();
            var verificationSettings = updateSettings.UpdateVerification ?? new UpdateVerificationSettings();
            var expectedTypeValues = verificationSettings.ExpectedTypeValues?.Count > 0
                ? verificationSettings.ExpectedTypeValues
                : new List<string>();

            if (!await SubmitUpdateFormAsync(
                    page,
                    canonicalBaseUrl,
                    updateOkData.PantherTypeIndex,
                    updateOkData.PantherName,
                    updateOkData.Weight,
                    updateOkData.Characteristics,
                    updateOkData.Warning,
                    date))
            {
                _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Submission error.");
                return false;
            }

            // Wait for page to settle after update submission
            await page.WaitForTimeoutAsync(1500);
            
            // Wait for navigation to complete (some projects go to detail page, some go to list page)
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
            }
            catch
            {
                // Ignore timeout, continue
            }
            
            // Check current page state after update
            var currentUrl = page.Url;
            var isOnListPage = IsListPageUrl(currentUrl);
            
            _logger.LogInformation("[Playwright] Q4 Update OK: After update - URL: {Url}, IsListPage: {IsList}", currentUrl, isOnListPage);
            
            // Navigate to the saved list page URL from Q1 login
            // This ensures we always go to the correct list page URL, regardless of where update redirects
            if (string.IsNullOrWhiteSpace(_listPageUrl))
            {
                _logger.LogWarning("[Playwright] Q4 Update OK: List page URL not saved from Q1, falling back to NavigateToListPageAsync...");
                if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Unable to navigate to list page after update.");
                    return false;
                }
            }
            else
            {
                _logger.LogInformation("[Playwright] Q4 Update OK: Navigating to saved list page URL from Q1: {Url}", _listPageUrl);
                try
                {
                    var response = await page.GotoAsync(_listPageUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                    if (response != null && response.Status >= 400)
                    {
                        _logger.LogWarning("[Playwright] Q4 Update OK: Failed to navigate to saved list URL (HTTP {Status}), falling back to NavigateToListPageAsync...", response.Status);
                        if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
                        {
                            _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Unable to navigate to list page after update.");
                            return false;
                        }
                    }
                    else
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                        await page.WaitForTimeoutAsync(1000);
                        _logger.LogInformation("[Playwright] Q4 Update OK: Successfully navigated to saved list page URL.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Q4 Update OK: Error navigating to saved list URL: {Message}, falling back to NavigateToListPageAsync...", ex.Message);
                    if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Unable to navigate to list page after update.");
                        return false;
                    }
                }
            }
            
            // Verify we're on list page now
            if (!IsListPageUrl(page.Url))
            {
                _logger.LogWarning("[Playwright] Q4 Update OK FAIL: Not on list page after navigation (current: {Url}).", page.Url);
                return false;
            }

            // Wait for table to be visible and rendered
            try
            {
                await page.WaitForSelectorAsync("table tbody tr", new() { Timeout = 5000 });
            }
            catch
            {
                _logger.LogWarning("[Playwright] Q4 Update OK: Table not found, but continuing...");
            }

            // Retry logic: try multiple times to find the updated row
            int maxRetries = 3;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                if (retry > 0)
                {
                    _logger.LogInformation("[Playwright] Q4 Update OK: Retry {Retry}/{MaxRetries} - Reloading page...", retry + 1, maxRetries);
                    await page.ReloadAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                    await page.WaitForTimeoutAsync(1000);
                }

                // Find row with configured weight and PantherType values
                // Check all rows to find the matching one
                try
                {
                    var rows = await page.Locator("table tbody tr").AllAsync();
                    if (rows.Count == 0)
                    {
                        _logger.LogWarning("[Playwright] Q4 Update OK: No rows found in table (retry {Retry}/{MaxRetries}).", retry + 1, maxRetries);
                        if (retry < maxRetries - 1) continue;
                        return false;
                    }

                // Get headers for flexible column finding
                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                
                // Log headers for debugging
                _logger.LogInformation("[Playwright] Q4 Update OK: Found {Count} headers: {Headers}", headerList.Count, string.Join(", ", headerList));
                
                // Find column indices (flexible) - prioritize TypeName over PantherType
                var weightIndex = FindColumnIndex(headerList, "Weight", "weight");
                var typeIndex = FindColumnIndex(headerList, "TypeName", "PantherType", "Type", "typename", "panthertype", "type");

                if (weightIndex < 0)
                {
                    _logger.LogWarning("[Playwright] Q4 Update OK: Weight column not found. Available headers: {Headers} (retry {Retry}/{MaxRetries})", string.Join(", ", headerList), retry + 1, maxRetries);
                    if (retry < maxRetries - 1) continue;
                    return false;
                }

                if (typeIndex < 0)
                {
                    _logger.LogWarning("[Playwright] Q4 Update OK: PantherType/TypeName column not found. Available headers: {Headers} (retry {Retry}/{MaxRetries})", string.Join(", ", headerList), retry + 1, maxRetries);
                    if (retry < maxRetries - 1) continue;
                    return false;
                }

                _logger.LogInformation("[Playwright] Q4 Update OK: Weight column index: {WeightIndex}, Type column index: {TypeIndex}", weightIndex, typeIndex);

                // Check all rows
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    try
                    {
                        var row = rows[rowIndex];
                        var cells = await row.Locator("td").AllAsync();
                        
                        if (cells.Count > weightIndex && cells.Count > typeIndex)
                        {
                            var weightText = (await cells[weightIndex].TextContentAsync())?.Trim() ?? string.Empty;
                            var typeText = (await cells[typeIndex].TextContentAsync())?.Trim() ?? string.Empty;

                            // Log with more detail including raw bytes for debugging
                            var typeTextBytes = System.Text.Encoding.UTF8.GetBytes(typeText);
                            _logger.LogInformation("[Playwright] Q4 Update OK: Row {RowIndex} - Weight: '{Weight}' (bytes: {WeightBytes}), Type: '{Type}' (bytes: {TypeBytes}, length: {TypeLength})", 
                                rowIndex, weightText, string.Join(",", System.Text.Encoding.UTF8.GetBytes(weightText)), typeText, string.Join(",", typeTextBytes), typeText.Length);

                            // Check Weight
                            bool weightMatch = false;
                            if (int.TryParse(weightText, out int weightValue))
                            {
                                weightMatch = int.TryParse(verificationSettings.ExpectedWeight, out var expectedWeightValue)
                                    ? weightValue == expectedWeightValue
                                    : weightText == verificationSettings.ExpectedWeight;
                            }
                            else
                            {
                                // Try string comparison (trimmed)
                                weightMatch = weightText == verificationSettings.ExpectedWeight ||
                                              weightText.Trim() == verificationSettings.ExpectedWeight;
                            }
                            
                            // Check PantherType against expected values
                            var normalizedTypeText = typeText.Trim();
                            // Normalize multiple spaces to single space for comparison
                            var normalizedType = System.Text.RegularExpressions.Regex.Replace(normalizedTypeText, @"\s+", " ");
                            
                            bool typeMatch = expectedTypeValues.Any(expected =>
                                normalizedType.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                                normalizedTypeText.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                                normalizedType.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                                normalizedTypeText.Replace(" ", "").Equals(expected.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ||
                                normalizedType.Replace(" ", "").Equals(expected.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

                            if (weightMatch && typeMatch)
                            {
                                _logger.LogInformation("[Playwright] Q4 Update OK PASS: Row {RowIndex} matches expected weight {ExpectedWeight} and type.", 
                                    rowIndex, verificationSettings.ExpectedWeight);
                                return true;
                            }
                            else
                            {
                                _logger.LogInformation("[Playwright] Q4 Update OK: Row {RowIndex} - Weight match: {WeightMatch}, Type match: {TypeMatch} (Weight: '{Weight}', Type: '{Type}')", 
                                    rowIndex, weightMatch, typeMatch, weightText, typeText);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("[Playwright] Q4 Update OK: Row {RowIndex} has {CellCount} cells, need at least {Required}", 
                                rowIndex, cells.Count, Math.Max(weightIndex, typeIndex) + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("[Playwright] Q4 Update OK: Error checking row {RowIndex}: {Message}", rowIndex, ex.Message);
                        continue;
                    }
                }

                    // If we get here, no matching row was found in this retry
                    _logger.LogWarning("[Playwright] Q4 Update OK: No row found with Weight={ExpectedWeight} and expected type values ({Types}) in {Count} rows (retry {Retry}/{MaxRetries}).", 
                        verificationSettings.ExpectedWeight, string.Join(", ", expectedTypeValues), rows.Count, retry + 1, maxRetries);
                    
                    // If this is not the last retry, continue to next retry
                    if (retry < maxRetries - 1)
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Q4 Update OK: Error verifying update (retry {Retry}/{MaxRetries}): {Message}", retry + 1, maxRetries, ex.Message);
                    if (retry < maxRetries - 1)
                    {
                        continue;
                    }
                }
            }

            // If we get here, all retries failed
            _logger.LogWarning("[Playwright] Q4 Update OK FAIL: No row found with Weight={ExpectedWeight} and expected type values ({Types}) after {MaxRetries} retries.", 
                verificationSettings.ExpectedWeight, string.Join(", ", expectedTypeValues), maxRetries);
            return false;
        }

        private async Task<double> RunQ5SearchTest(IPage page, string canonicalBaseUrl)
        {
            double score = 0.0;
            double test1Score = 0.0;
            string test1Note = string.Empty;
            double test2Score = 0.0;
            string test2Note = string.Empty;
            double test3Score = 0.0;
            string test3Note = string.Empty;
            var searchSettings = _settings.Search ?? new SearchTestSettings();

            try
            {
                // Ensure we're on list page first
                if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
                {
                    _logger.LogWarning("[Playwright] Q5: Unable to reach list page before search test. Score set to 0.");
                    return 0;
                }

                // Check if search inputs (Weight and TypeName) are already available on list page
                // Some projects have search inputs directly on list page, others need to navigate to search page
                var hasSearchInputsOnListPage = await CheckSearchInputsOnListPageAsync(page);
                
                if (!hasSearchInputsOnListPage)
                {
                    // Search inputs not found on list page, need to find Search button/link to navigate to search page
                    _logger.LogInformation("[Playwright] Q5: Search inputs not found on list page, looking for Search button/link to navigate...");
                    
                    if (!await NavigateToSearchPageAsync(page, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q5: Unable to navigate to search page. Score set to 0.");
                        return 0;
                    }

                    // Verify search page has required elements after navigation
                    if (!await VerifySearchPageAsync(page))
                    {
                        _logger.LogWarning("[Playwright] Q5: Search page missing required elements (Weight input, TypeName input, Search button). Score set to 0.");
                        return 0;
                    }
                    
                    _logger.LogInformation("[Playwright] Q5: Successfully navigated to search page.");
                }
                else
                {
                    // Search inputs found on list page, can use them directly
                    _logger.LogInformation("[Playwright] Q5: Search inputs (Weight and TypeName) found on list page, no navigation needed. Will use them directly.");
                }

                // Test 1: Search by Weight=100, TypeName empty -> expect 2 panthers, both with Weight=100
                test1Score = await RunQ5Test1Async(page, canonicalBaseUrl, searchSettings.Test1);
                if (test1Score < 0.5)
                {
                    test1Note = "Fail test case: Search by Weight did not return expected results";
                }
                score += test1Score;
                _logger.LogInformation("[Playwright] Q5 Test 1 Score: {Score}/0.5", test1Score);

                // Test 2: Search by TypeName="Black leopards", Weight empty -> expect 3 panthers, all with PantherType="Black leopards"
                test2Score = await RunQ5Test2Async(page, canonicalBaseUrl, searchSettings.Test2);
                if (test2Score < 0.5)
                {
                    test2Note = "Fail test case: Search by TypeName did not return expected results";
                }
                score += test2Score;
                _logger.LogInformation("[Playwright] Q5 Test 2 Score: {Score}/0.5", test2Score);

                // Test 3: Search by Weight=120 OR TypeName="Black leopards" -> expect 3 panthers, each must satisfy at least one condition (Weight=120 OR PantherType="Black leopards"/"1")
                test3Score = await RunQ5Test3Async(page, canonicalBaseUrl, searchSettings.Test3);
                if (test3Score < 0.5)
                {
                    test3Note = "Fail test case: Search by Weight or TypeName did not return expected results";
                }
                score += test3Score;
                _logger.LogInformation("[Playwright] Q5 Test 3 Score: {Score}/0.5", test3Score);
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q5: Critical error during search test: {Message}", ex.Message);
            }

            // Populate TestResultDetail
            if (_testResultDetail != null)
            {
                _testResultDetail.Q5Test1 = test1Score;
                _testResultDetail.Q5Test1Note = test1Note;
                _testResultDetail.Q5Test2 = test2Score;
                _testResultDetail.Q5Test2Note = test2Note;
                _testResultDetail.Q5Test3 = test3Score;
                _testResultDetail.Q5Test3Note = test3Note;
            }

            return Math.Min(1.5, Math.Max(0, score));
        }

        private async Task<bool> NavigateToSearchPageAsync(IPage page, string canonicalBaseUrl)
        {
            // Try to find search button/link on current page
            var searchSelectors = new[]
            {
                "a:has-text('Search')",
                "a:has-text('search')",
                "button:has-text('Search')",
                "button:has-text('search')",
                "a[href*='/Search']",
                "a[href*='/PantherProfile/Search']",
                "a[href*='/PantherProfiles/Search']",
                "[aria-label*='search' i]",
                "[title*='search' i]"
            };

            foreach (var selector in searchSelectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        await page.WaitForTimeoutAsync(1000);
                        if (await VerifySearchPageAsync(page))
                        {
                            _logger.LogInformation("[Playwright] Q5: Navigated to search page via selector {Selector}", selector);
                            return true;
                        }
                    }
                }
                catch
                {
                    // Try next selector
                }
            }

            // Fallback: try direct URL navigation
            var searchUrls = BuildSearchUrls(canonicalBaseUrl, page.Url);
            foreach (var searchUrl in searchUrls)
            {
                try
                {
                    var response = await page.GotoAsync(searchUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                    if (response != null && response.Status >= 400)
                    {
                        continue;
                    }
                    await page.WaitForTimeoutAsync(1000);
                    if (await VerifySearchPageAsync(page))
                    {
                        _logger.LogInformation("[Playwright] Q5: Navigated to search page via URL {Url}", searchUrl);
                        return true;
                    }
                }
                catch
                {
                    // Try next URL
                }
            }

            return false;
        }

        private static IEnumerable<string> BuildSearchUrls(string canonicalBaseUrl, string currentUrl)
        {
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                var markerIndex1 = currentUrl.LastIndexOf("/PantherProfile", StringComparison.OrdinalIgnoreCase);
                var markerIndex2 = currentUrl.LastIndexOf("/PantherProfiles", StringComparison.OrdinalIgnoreCase);

                if (markerIndex1 >= 0)
                {
                    var prefix = currentUrl.Substring(0, markerIndex1);
                    candidates.Add($"{prefix}/PantherProfile/Search");
                    candidates.Add($"{prefix}/PantherProfiles/Search");
                }
                else if (markerIndex2 >= 0)
                {
                    var prefix = currentUrl.Substring(0, markerIndex2);
                    candidates.Add($"{prefix}/PantherProfiles/Search");
                    candidates.Add($"{prefix}/PantherProfile/Search");
                }
            }

            var baseUrl = canonicalBaseUrl.TrimEnd('/');
            candidates.Add($"{baseUrl}/PantherProfile/Search");
            candidates.Add($"{baseUrl}/PantherProfiles/Search");

            return candidates.Distinct();
        }

        /// <summary>
        /// Check if Weight and TypeName search inputs are available on the current page (list page)
        /// This is used to determine if we can search directly on list page or need to navigate to search page
        /// </summary>
        private async Task<bool> CheckSearchInputsOnListPageAsync(IPage page)
        {
            try
            {
                // Check for Weight input - more flexible approach
                bool hasWeightInput = false;
                
                // First try standard selectors
                var weightSelectors = new[]
                {
                    "input[name*='Weight' i]",
                    "input[id*='Weight' i]",
                    "input[placeholder*='Weight' i]",
                    "input[type='number'][name*='weight' i]"
                };

                foreach (var selector in weightSelectors)
                {
                    try
                    {
                        if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            hasWeightInput = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found, try to find all inputs and check placeholder/text content
                if (!hasWeightInput)
                {
                    try
                    {
                        var allInputs = await page.Locator("input").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var inputType = await input.GetAttributeAsync("type") ?? string.Empty;
                                // Skip submit, button, hidden inputs
                                if (inputType.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("Weight", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        hasWeightInput = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                // Check for TypeName input - more flexible approach
                bool hasTypeNameInput = false;
                
                // First try standard selectors
                var typeNameSelectors = new[]
                {
                    "input[name*='TypeName' i]",
                    "input[name*='PantherTypeName' i]",
                    "input[id*='TypeName' i]",
                    "input[id*='PantherTypeName' i]",
                    "input[placeholder*='Type Name' i]",
                    "input[placeholder*='Panther Type Name' i]",
                    "input[placeholder*='TypeName' i]"
                };

                foreach (var selector in typeNameSelectors)
                {
                    try
                    {
                        if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            hasTypeNameInput = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found, try to find all inputs and check placeholder/text content
                if (!hasTypeNameInput)
                {
                    try
                    {
                        var allInputs = await page.Locator("input").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var inputType = await input.GetAttributeAsync("type") ?? string.Empty;
                                // Skip submit, button, hidden inputs
                                if (inputType.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    placeholder.Contains("Type Name", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        hasTypeNameInput = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                bool result = hasWeightInput && hasTypeNameInput;
                if (result)
                {
                    _logger.LogInformation("[Playwright] Q5: Found search inputs on list page - Weight: {Weight}, TypeName: {TypeName}", hasWeightInput, hasTypeNameInput);
                }
                else
                {
                    _logger.LogInformation("[Playwright] Q5: Search inputs not found on list page - Weight: {Weight}, TypeName: {TypeName}", hasWeightInput, hasTypeNameInput);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5: CheckSearchInputsOnListPageAsync error: {Message}", ex.Message);
                return false;
            }
        }

        private async Task<bool> VerifySearchPageAsync(IPage page)
        {
            try
            {
                // Check for Weight input - more flexible approach
                bool hasWeightInput = false;
                
                // First try standard selectors
                var weightSelectors = new[]
                {
                    "input[name*='Weight' i]",
                    "input[id*='Weight' i]",
                    "input[placeholder*='Weight' i]",
                    "input[type='number'][name*='weight' i]"
                };

                foreach (var selector in weightSelectors)
                {
                    try
                    {
                        if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            hasWeightInput = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found, try to find all inputs and check placeholder/text content
                if (!hasWeightInput)
                {
                    try
                    {
                        // Try more flexible selector - get all inputs except submit/button/hidden
                        var allInputs = await page.Locator("input").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var inputType = await input.GetAttributeAsync("type") ?? string.Empty;
                                // Skip submit, button, hidden inputs
                                if (inputType.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("Weight", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        _logger.LogInformation("[Playwright] Q5: Found Weight input via placeholder/name/id: placeholder='{Placeholder}', name='{Name}', id='{Id}'", placeholder, name, id);
                                        hasWeightInput = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                // Check for TypeName input - more flexible approach
                bool hasTypeNameInput = false;
                
                // First try standard selectors
                var typeNameSelectors = new[]
                {
                    "input[name*='TypeName' i]",
                    "input[name*='PantherTypeName' i]",
                    "input[id*='TypeName' i]",
                    "input[id*='PantherTypeName' i]",
                    "input[placeholder*='Type Name' i]",
                    "input[placeholder*='Panther Type Name' i]",
                    "input[placeholder*='TypeName' i]"
                };

                foreach (var selector in typeNameSelectors)
                {
                    try
                    {
                        if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            hasTypeNameInput = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found, try to find all inputs and check placeholder/text content
                if (!hasTypeNameInput)
                {
                    try
                    {
                        // Try more flexible selector - get all inputs except submit/button/hidden
                        var allInputs = await page.Locator("input").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var inputType = await input.GetAttributeAsync("type") ?? string.Empty;
                                // Skip submit, button, hidden inputs
                                if (inputType.Equals("submit", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                                    inputType.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    placeholder.Contains("Type Name", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        _logger.LogInformation("[Playwright] Q5: Found TypeName input via placeholder/name/id: placeholder='{Placeholder}', name='{Name}', id='{Id}'", placeholder, name, id);
                                        hasTypeNameInput = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                // Check for Search button
                var searchButtonSelectors = new[]
                {
                    "button:has-text('Search')",
                    "button:has-text('search')",
                    "input[type='submit'][value*='Search' i]",
                    "button[type='submit']",
                    "[aria-label*='search' i]",
                    "[title*='search' i]",
                    "button:has([class*='search' i])",
                    "button:has([class*='magnifying' i])"
                };

                bool hasSearchButton = false;
                foreach (var selector in searchButtonSelectors)
                {
                    try
                    {
                        if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            hasSearchButton = true;
                            break;
                        }
                    }
                    catch { }
                }

                bool result = hasWeightInput && hasTypeNameInput && hasSearchButton;
                if (!result)
                {
                    _logger.LogInformation("[Playwright] Q5: VerifySearchPageAsync - Weight input: {Weight}, TypeName input: {TypeName}, Search button: {Search}", 
                        hasWeightInput, hasTypeNameInput, hasSearchButton);
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5: VerifySearchPageAsync error: {Message}", ex.Message);
                return false;
            }
        }

        private async Task<double> RunQ5Test1Async(IPage page, string canonicalBaseUrl, SearchTestCaseSettings? testCase)
        {
            try
            {
                if (!await EnsureSearchPageAsync(page, canonicalBaseUrl))
                {
                    return 0;
                }

                // Fill Weight=100, leave TypeName empty
                var weightValue = testCase?.Weight ?? "100";
                if (!await FillAndSubmitSearchAsync(page, weightValue, testCase?.TypeName ?? string.Empty))
                {
                    _logger.LogWarning("[Playwright] Q5 Test 1 FAIL: Search submission error.");
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }

                // Verify results: expect 2 panthers, both with Weight=100
                var expectedCount = ResolveExpectedSearchCount(testCase);
                var (count, allHaveWeight100) = await VerifySearchResultsByWeightAsync(page, weightValue);
                if (count == expectedCount && allHaveWeight100)
                {
                    _logger.LogInformation("[Playwright] Q5 Test 1 PASS: Found {Count} panthers, all with Weight={Weight}", count, weightValue);
                    return 0.5;
                }
                else
                {
                    _logger.LogWarning("[Playwright] Q5 Test 1 FAIL: Expected {Expected} panthers with Weight={Weight}, got {Count} (all correct: {AllCorrect})", 
                        expectedCount, weightValue, count, allHaveWeight100);
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5 Test 1 FAIL: {Message}", ex.Message);
                await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                return 0;
            }
        }

        private async Task<double> RunQ5Test2Async(IPage page, string canonicalBaseUrl, SearchTestCaseSettings? testCase)
        {
            try
            {
                if (!await EnsureSearchPageAsync(page, canonicalBaseUrl))
                {
                    return 0;
                }

                // Fill TypeName="Black leopards", leave Weight empty
                var typeName = testCase?.TypeName ?? "Black leopards";
                if (!await FillAndSubmitSearchAsync(page, testCase?.Weight ?? string.Empty, typeName))
                {
                    _logger.LogWarning("[Playwright] Q5 Test 2 FAIL: Search submission error.");
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }

                // Verify results: adjust expectations based on Q3 Display Top result
                int expectedCount = ResolveExpectedSearchCount(testCase);
                var (count, allHaveType) = await VerifySearchResultsByTypeAsync(page, typeName);
                
                if (count == expectedCount && allHaveType)
                {
                    _logger.LogInformation("[Playwright] Q5 Test 2 PASS: Found {Count} panthers (expected {Expected}), all with PantherType='{TypeName}'", 
                        count, expectedCount, typeName);
                    return 0.5;
                }
                else
                {
                    _logger.LogWarning("[Playwright] Q5 Test 2 FAIL: Expected {Expected} panthers with PantherType='{TypeName}', got {Count} (all correct: {AllCorrect})", 
                        expectedCount, typeName, count, allHaveType);
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5 Test 2 FAIL: {Message}", ex.Message);
                await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                return 0;
            }
        }

        private async Task<double> RunQ5Test3Async(IPage page, string canonicalBaseUrl, SearchTestCaseSettings? testCase)
        {
            try
            {
                if (!await EnsureSearchPageAsync(page, canonicalBaseUrl))
                {
                    return 0;
                }

                // Fill Weight=120 OR TypeName="Black leopards" (search with OR logic)
                var weightValue = testCase?.Weight ?? "120";
                var typeName = testCase?.TypeName ?? "Black leopards";
                if (!await FillAndSubmitSearchAsync(page, weightValue, typeName))
                {
                    _logger.LogWarning("[Playwright] Q5 Test 3 FAIL: Search submission error.");
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }

                // Verify results: adjust expectations based on Q3 Display Top result
                // Each panther must satisfy at least one condition: Weight=weightValue OR PantherType=typeName
                int expectedCount = ResolveExpectedSearchCount(testCase);
                var (count, allCorrect) = await VerifySearchResultsOrAsync(page, weightValue, typeName);
                
                if (count == expectedCount && allCorrect)
                {
                    _logger.LogInformation("[Playwright] Q5 Test 3 PASS: Found {Count} panthers (expected {Expected}), all satisfy Weight={Weight} OR PantherType='{TypeName}'", 
                        count, expectedCount, weightValue, typeName);
                    return 0.5;
                }
                else
                {
                    _logger.LogWarning("[Playwright] Q5 Test 3 FAIL: Expected {Expected} panthers with Weight={Weight} OR PantherType='{TypeName}', got {Count} (all correct: {AllCorrect})", 
                        expectedCount, weightValue, typeName, count, allCorrect);
                    await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5 Test 3 FAIL: {Message}", ex.Message);
                await NavigateBackToSearchAsync(page, canonicalBaseUrl);
                return 0;
            }
        }

        private int ResolveExpectedSearchCount(SearchTestCaseSettings? testCase)
        {
            if (testCase == null)
            {
                return 0;
            }

            var fallback = testCase.ExpectedCountWhenDisplayTopFail ?? testCase.ExpectedCountWhenDisplayTopPass;
            if (!testCase.AdjustByQ3DisplayTop)
            {
                return testCase.ExpectedCountWhenDisplayTopPass;
            }

            return _q3DisplayTopPassed ? testCase.ExpectedCountWhenDisplayTopPass : fallback;
        }

        private async Task<bool> EnsureSearchPageAsync(IPage page, string canonicalBaseUrl)
        {
            if (await VerifySearchPageAsync(page))
            {
                return true;
            }
            return await NavigateToSearchPageAsync(page, canonicalBaseUrl);
        }

        private async Task<bool> NavigateBackToSearchAsync(IPage page, string canonicalBaseUrl)
        {
            try
            {
                // Try browser back
                await page.GoBackAsync();
                await page.WaitForTimeoutAsync(1000);
                if (await VerifySearchPageAsync(page))
                {
                    return true;
                }
            }
            catch { }

            return await NavigateToSearchPageAsync(page, canonicalBaseUrl);
        }

        private async Task<bool> FillAndSubmitSearchAsync(IPage page, string weight, string typeName)
        {
            try
            {
                // Find and fill Weight input - more flexible approach
                var weightSelectors = new[]
                {
                    "input[name*='Weight' i]",
                    "input[id*='Weight' i]",
                    "input[placeholder*='Weight' i]"
                };

                bool weightFilled = false;
                foreach (var selector in weightSelectors)
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            await locator.ClearAsync();
                            await locator.FillAsync(weight ?? string.Empty);
                            weightFilled = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found by selectors, try to find by checking all inputs
                if (!weightFilled)
                {
                    try
                    {
                        var allInputs = await page.Locator("input[type='text'], input[type='number'], input:not([type='submit']):not([type='button']):not([type='hidden'])").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Weight", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("Weight", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        await input.ClearAsync();
                                        await input.FillAsync(weight ?? string.Empty);
                                        weightFilled = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                // Find and fill TypeName input - more flexible approach
                var typeNameSelectors = new[]
                {
                    "input[name*='TypeName' i]",
                    "input[name*='PantherTypeName' i]",
                    "input[id*='TypeName' i]",
                    "input[id*='PantherTypeName' i]",
                    "input[placeholder*='Type Name' i]",
                    "input[placeholder*='Panther Type Name' i]",
                    "input[placeholder*='TypeName' i]"
                };

                bool typeNameFilled = false;
                foreach (var selector in typeNameSelectors)
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            await locator.ClearAsync();
                            await locator.FillAsync(typeName ?? string.Empty);
                            typeNameFilled = true;
                            break;
                        }
                    }
                    catch { }
                }

                // If not found by selectors, try to find by checking all inputs
                if (!typeNameFilled)
                {
                    try
                    {
                        var allInputs = await page.Locator("input[type='text'], input[type='number'], input:not([type='submit']):not([type='button']):not([type='hidden'])").AllAsync();
                        foreach (var input in allInputs)
                        {
                            try
                            {
                                var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
                                var name = await input.GetAttributeAsync("name") ?? string.Empty;
                                var id = await input.GetAttributeAsync("id") ?? string.Empty;
                                
                                if (placeholder.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    placeholder.Contains("Type Name", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("TypeName", StringComparison.OrdinalIgnoreCase) ||
                                    id.Contains("PantherTypeName", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (await input.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        await input.ClearAsync();
                                        await input.FillAsync(typeName ?? string.Empty);
                                        typeNameFilled = true;
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }
                }

                if (!weightFilled || !typeNameFilled)
                {
                    _logger.LogWarning("[Playwright] Q5: Could not fill search form. Weight filled: {Weight}, TypeName filled: {TypeName}", weightFilled, typeNameFilled);
                    return false;
                }

                // Find and click Search button
                var searchButtonSelectors = new[]
                {
                    "button:has-text('Search')",
                    "button:has-text('search')",
                    "input[type='submit'][value*='Search' i]",
                    "button[type='submit']"
                };

                foreach (var selector in searchButtonSelectors)
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                            await page.WaitForTimeoutAsync(1000);
                            try
                            {
                                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 2000 });
                            }
                            catch { }

                            // Check for fatal page state (error page)
                            if (!await CheckForFatalPageStateAsync(page))
                            {
                                _logger.LogWarning("[Playwright] Q5: Fatal page state detected after search submission.");
                                return false;
                            }

                            return true;
                        }
                    }
                    catch { }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q5: Error filling/submitting search form: {Message}", ex.Message);
                return false;
            }
        }

        private async Task<(int Count, bool AllHaveWeight)> VerifySearchResultsByWeightAsync(IPage page, string expectedWeight)
        {
            try
            {
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    return (0, false);
                }

                // Find Weight column index (flexible)
                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                var weightIndex = FindColumnIndex(headerList, "Weight", "weight");

                if (weightIndex < 0)
                {
                    return (rows.Count, false);
                }

                int correctCount = 0;
                foreach (var row in rows)
                {
                    var cells = await row.Locator("td").AllAsync();
                    if (cells.Count > weightIndex)
                    {
                        var weightText = await cells[weightIndex].TextContentAsync();
                        if (!string.IsNullOrEmpty(weightText) && weightText.Trim() == expectedWeight)
                        {
                            correctCount++;
                        }
                    }
                }

                return (rows.Count, correctCount == rows.Count && rows.Count > 0);
            }
            catch
            {
                return (0, false);
            }
        }

        private async Task<(int Count, bool AllHaveType)> VerifySearchResultsByTypeAsync(IPage page, string expectedType)
        {
            try
            {
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    return (0, false);
                }

                // Find Type column index (flexible: PantherType, TypeName, Type)
                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                var typeIndex = FindColumnIndex(headerList, "PantherType", "TypeName", "Type", "panthertype", "typename", "type");

                if (typeIndex < 0)
                {
                    return (rows.Count, false);
                }

                var allowedTypeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expectedType };
                foreach (var alias in GetTypeAliases(expectedType))
                {
                    allowedTypeValues.Add(alias);
                }

                int correctCount = 0;
                foreach (var row in rows)
                {
                    var cells = await row.Locator("td").AllAsync();
                    if (cells.Count > typeIndex)
                    {
                        var typeText = await cells[typeIndex].TextContentAsync();
                        if (!string.IsNullOrEmpty(typeText) && allowedTypeValues.Contains(typeText.Trim()))
                        {
                            correctCount++;
                        }
                    }
                }

                return (rows.Count, correctCount == rows.Count && rows.Count > 0);
            }
            catch
            {
                return (0, false);
            }
        }

        private async Task<(int Count, bool IsCorrect)> VerifySearchResultsCombinedAsync(IPage page, string expectedWeight, string expectedType, string expectedName)
        {
            try
            {
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count != 1)
                {
                    return (rows.Count, false);
                }

                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                
                // Find column indices (flexible)
                var weightIndex = FindColumnIndex(headerList, "Weight", "weight");
                var typeIndex = FindColumnIndex(headerList, "PantherType", "TypeName", "Type", "panthertype", "typename", "type");
                var nameIndex = FindColumnIndex(headerList, "PantherName", "panthername", "Name", "name");

                if (weightIndex < 0 || typeIndex < 0 || nameIndex < 0)
                {
                    return (1, false);
                }

                var row = rows[0];
                var cells = await row.Locator("td").AllAsync();

                var weightText = cells.Count > weightIndex ? (await cells[weightIndex].TextContentAsync())?.Trim() ?? string.Empty : string.Empty;
                var typeText = cells.Count > typeIndex ? (await cells[typeIndex].TextContentAsync())?.Trim() ?? string.Empty : string.Empty;
                var nameText = cells.Count > nameIndex ? (await cells[nameIndex].TextContentAsync())?.Trim() ?? string.Empty : string.Empty;

                var weightMatch = weightText == expectedWeight;
                var typeMatch = typeText.Equals(expectedType, StringComparison.OrdinalIgnoreCase);
                var nameMatch = nameText.Equals(expectedName, StringComparison.OrdinalIgnoreCase);

                return (1, weightMatch && typeMatch && nameMatch);
            }
            catch
            {
                return (0, false);
            }
        }

        /// <summary>
        /// Verify search results with OR logic: each row must satisfy at least one condition
        /// </summary>
        private async Task<(int Count, bool AllCorrect)> VerifySearchResultsOrAsync(IPage page, string expectedWeight, string expectedType)
        {
            try
            {
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    return (0, false);
                }

                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                
                // Find column indices (flexible)
                var weightIndex = FindColumnIndex(headerList, "Weight", "weight");
                var typeIndex = FindColumnIndex(headerList, "PantherType", "TypeName", "Type", "panthertype", "typename", "type");

                if (weightIndex < 0 && typeIndex < 0)
                {
                    return (rows.Count, false);
                }

                var allowedTypeValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expectedType };
                foreach (var alias in GetTypeAliases(expectedType))
                {
                    allowedTypeValues.Add(alias);
                }

                int correctCount = 0;
                foreach (var row in rows)
                {
                    var cells = await row.Locator("td").AllAsync();
                    
                    var weightText = weightIndex >= 0 && cells.Count > weightIndex 
                        ? (await cells[weightIndex].TextContentAsync())?.Trim() ?? string.Empty 
                        : string.Empty;
                    
                    var typeText = typeIndex >= 0 && cells.Count > typeIndex 
                        ? (await cells[typeIndex].TextContentAsync())?.Trim() ?? string.Empty 
                        : string.Empty;

                    // Check if row satisfies at least one condition:
                    // - Weight = expectedWeight OR
                    // - PantherType = expectedType OR "1" (if expectedType is "Black leopards")
                    bool weightMatch = false;
                    bool typeMatch = false;

                    if (weightIndex >= 0 && !string.IsNullOrEmpty(weightText))
                    {
                        weightMatch = weightText == expectedWeight;
                    }

                    if (typeIndex >= 0 && !string.IsNullOrEmpty(typeText))
                    {
                        var normalizedType = typeText.Trim();
                        typeMatch = allowedTypeValues.Contains(normalizedType);
                    }

                    // Row is correct if it satisfies at least one condition (OR logic)
                    if (weightMatch || typeMatch)
                    {
                        correctCount++;
                    }
                }

                // All rows must be correct
                return (rows.Count, correctCount == rows.Count && rows.Count > 0);
            }
            catch
            {
                return (0, false);
            }
        }

        private IEnumerable<string> GetTypeAliases(string expectedType)
        {
            foreach (var kvp in PantherTypeOptionMap)
            {
                if (kvp.Value.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                {
                    yield return kvp.Key;
                }
            }
        }

        private async Task<double> RunQ6DeleteTest(IPage leftPage, string canonicalBaseUrl, IBrowserContext context)
        {
            double score = 0.0;
            double deleteWithSignalRScore = 0.0;
            string deleteWithSignalRNote = string.Empty;

            try
            {
                // Ensure we're on list page (explicitly navigate from search or any other page)
                _logger.LogInformation("[Playwright] Q6: Current URL before navigation: {Url}", leftPage.Url);
                
                if (!IsListPageUrl(leftPage.Url))
                {
                    _logger.LogInformation("[Playwright] Q6: Not on list page, navigating to list page...");
                    
                    // Try to find and click "Back to List" or similar button first
                    var backToListSelectors = new[]
                    {
                        "a:has-text('Back to List')",
                        "a:has-text('Back to list')",
                        "a:has-text('Back')",
                        "button:has-text('Back to List')",
                        "a[href*='/PantherProfile']:not([href*='/Search']):not([href*='/Create']):not([href*='/Edit'])",
                        "a[href*='/PantherProfiles']:not([href*='/Search']):not([href*='/Create']):not([href*='/Edit'])"
                    };

                    bool navigatedViaButton = false;
                    foreach (var selector in backToListSelectors)
                    {
                        try
                        {
                            var locator = leftPage.Locator(selector).First;
                            if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                            {
                                await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                                await leftPage.WaitForTimeoutAsync(1000);
                                if (IsListPageUrl(leftPage.Url))
                                {
                                    _logger.LogInformation("[Playwright] Q6: Navigated to list via button: {Selector}", selector);
                                    navigatedViaButton = true;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    if (!navigatedViaButton)
                    {
                        // Fallback to direct URL navigation
                        if (!await NavigateToListPageAsync(leftPage, canonicalBaseUrl))
                        {
                            _logger.LogWarning("[Playwright] Q6: Unable to reach list page before delete test. Score set to 0.");
                            return 0;
                        }
                    }
                }
                else
                {
                    _logger.LogInformation("[Playwright] Q6: Already on list page");
                }

                await leftPage.WaitForTimeoutAsync(1000);
                try
                {
                    await leftPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                }
                catch { }

                // Verify we're actually on list page with table
                var hasTable = await leftPage.Locator("table tbody tr").CountAsync() > 0;
                if (!hasTable)
                {
                    _logger.LogWarning("[Playwright] Q6: List page loaded but no table rows found. Score set to 0.");
                    return 0;
                }

                _logger.LogInformation("[Playwright] Q6: Successfully on list page with table data");

                // Get first row and check for delete button
                var rows = await leftPage.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    _logger.LogWarning("[Playwright] Q6: No rows found in table. Score set to 0.");
                    return 0;
                }

                var firstRow = rows[0];
                
                // Get PantherName from first row (flexible column name)
                var headers = await leftPage.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                var nameIndex = FindColumnIndex(headerList, "PantherName", "panthername", "Name", "name");

                if (nameIndex < 0)
                {
                    _logger.LogWarning("[Playwright] Q6: PantherName column not found. Score set to 0.");
                    return 0;
                }

                var firstRowCells = await firstRow.Locator("td").AllAsync();
                if (firstRowCells.Count <= nameIndex)
                {
                    _logger.LogWarning("[Playwright] Q6: First row does not have enough cells. Score set to 0.");
                    return 0;
                }

                var pantherName = (await firstRowCells[nameIndex].TextContentAsync())?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(pantherName))
                {
                    _logger.LogWarning("[Playwright] Q6: Could not get PantherName from first row. Score set to 0.");
                    return 0;
                }

                _logger.LogInformation("[Playwright] Q6: First row PantherName: {Name}", pantherName);

                // Check for delete button in first row (try last cell first, then entire row, then Actions column)
                ILocator? deleteButton = null;
                
                // Try last cell first
                var lastCell = firstRowCells[firstRowCells.Count - 1];
                deleteButton = await FindDeleteButtonInCellAsync(lastCell);
                
                // If not found, try entire row
                if (deleteButton == null)
                {
                    deleteButton = await FindDeleteButtonInRowAsync(firstRow);
                }
                
                // If still not found, try Actions column specifically
                if (deleteButton == null)
                {
                    var actionsIndex = -1;
                    for (int i = 0; i < headers.Count; i++)
                    {
                        if (headers[i].Contains("Actions", StringComparison.OrdinalIgnoreCase))
                        {
                            actionsIndex = i;
                            break;
                        }
                    }
                    
                    if (actionsIndex >= 0 && firstRowCells.Count > actionsIndex)
                    {
                        var actionsCell = firstRowCells[actionsIndex];
                        deleteButton = await FindDeleteButtonInCellAsync(actionsCell);
                    }
                }
                
                if (deleteButton == null)
                {
                    _logger.LogWarning("[Playwright] Q6: Delete button not found in first row. Score set to 0.");
                    return 0;
                }

                _logger.LogInformation("[Playwright] Q6: Delete button found in first row");

                // Create second page (right side) for realtime update check
                var rightPage = await context.NewPageAsync();
                try
                {
                    // Navigate right page to list
                    if (!await NavigateToListPageAsync(rightPage, canonicalBaseUrl))
                    {
                        _logger.LogWarning("[Playwright] Q6: Unable to navigate right page to list. Score set to 0.");
                        return 0;
                    }

                    // Wait longer for right page to fully load
                    await rightPage.WaitForTimeoutAsync(2000);
                    try
                    {
                        await rightPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 });
                    }
                    catch { }

                    // Wait for table to be visible
                    try
                    {
                        await rightPage.Locator("table tbody tr").First.WaitForAsync(new() { Timeout = 3000 });
                    }
                    catch { }

                    // Verify panther exists in right page before delete (search in all pages if needed)
                    var existsBefore = await VerifyPantherExistsInListWithPaginationAsync(rightPage, pantherName);
                    if (!existsBefore)
                    {
                        _logger.LogWarning("[Playwright] Q6: Panther {Name} not found in right page before delete. Will continue anyway to test realtime update.", pantherName);
                        // Don't return 0, continue to test realtime update - maybe panther is on different page
                    }
                    else
                    {
                        _logger.LogInformation("[Playwright] Q6: Panther {Name} confirmed in right page before delete", pantherName);
                    }

                    // Setup dialog handler for confirm dialog (must be before clicking)
                    string? dialogMessage = null;
                    var dialogTask = new TaskCompletionSource<IDialog>();
                    
                    leftPage.Dialog += (sender, e) =>
                    {
                        dialogMessage = e.Message;
                        _logger.LogInformation("[Playwright] Q6: Dialog detected: {Message}", dialogMessage);
                        dialogTask.TrySetResult(e);
                    };

                    // Click delete button on left page
                    await deleteButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                    
                    // Wait a bit for dialog or on-page confirm to appear
                    await leftPage.WaitForTimeoutAsync(1500);

                    // Check for confirm dialog/alert
                    var hasConfirm = false;
                    bool confirmButtonClicked = false;
                    
                    try
                    {
                        // Try to wait for dialog with longer timeout
                        var dialog = await dialogTask.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        hasConfirm = await CheckForConfirmDialogAsync(leftPage, dialogMessage);
                        if (hasConfirm)
                        {
                            await dialog.AcceptAsync();
                            _logger.LogInformation("[Playwright] Q6: Confirm dialog detected and accepted");
                        }
                        else if (!string.IsNullOrEmpty(dialogMessage))
                        {
                            // Dialog appeared but message doesn't match confirm pattern - still count as confirm
                            hasConfirm = true;
                            await dialog.AcceptAsync();
                            _logger.LogInformation("[Playwright] Q6: Dialog detected (message: {Message}) and accepted", dialogMessage);
                        }
                    }
                    catch
                    {
                        // No dialog appeared, wait for page to load (might navigate to delete confirmation page)
                        await leftPage.WaitForTimeoutAsync(1000);
                        
                        // Wait for page to be ready
                        try
                        {
                            await leftPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 2000 });
                        }
                        catch { }
                        
                        // Check for on-page confirm elements
                        hasConfirm = await CheckForConfirmDialogAsync(leftPage, null);
                        _logger.LogInformation("[Playwright] Q6: CheckForConfirmDialogAsync returned: {HasConfirm}", hasConfirm);
                        
                        if (hasConfirm)
                        {
                            confirmButtonClicked = await AcceptConfirmDialogAsync(leftPage);
                            if (confirmButtonClicked)
                            {
                                _logger.LogInformation("[Playwright] Q6: On-page confirm detected and accepted");
                            }
                        }
                        else
                        {
                            // Try to find and click confirm button anyway (might be a form-based confirm page)
                            confirmButtonClicked = await AcceptConfirmDialogAsync(leftPage);
                            if (confirmButtonClicked)
                            {
                                hasConfirm = true;
                                _logger.LogInformation("[Playwright] Q6: Confirm button found and clicked (form-based confirm)");
                            }
                            else
                            {
                                // Final check: read page body text to see if confirm message exists
                                try
                                {
                                    var pageText = await leftPage.Locator("body").InnerTextAsync();
                                    var confirmKeywords = new[]
                                    {
                                        "Are you sure you want to delete this Profile",
                                        "Are you sure you want to delete this",
                                        "are you sure",
                                        "delete this Profile",
                                        "delete this profile"
                                    };
                                    
                                    bool hasConfirmKeyword = confirmKeywords.Any(keyword => 
                                        pageText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                                    
                                    if (hasConfirmKeyword)
                                    {
                                        hasConfirm = true;
                                        _logger.LogInformation("[Playwright] Q6: Confirm message detected in page body text, setting hasConfirm=true");
                                        // Try to click confirm button again
                                        confirmButtonClicked = await AcceptConfirmDialogAsync(leftPage);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning("[Playwright] Q6: Error checking page body text: {Message}", ex.Message);
                                }
                            }
                        }
                    }

                    // Test Case 1: Confirm Dialog (0.5 points)
                    if (hasConfirm || confirmButtonClicked)
                    {
                        score += 0.5;
                        _logger.LogInformation("[Playwright] Q6 Test Case 1 (Confirm Dialog) PASS: hasConfirm={HasConfirm}, confirmButtonClicked={Clicked}. Score: +0.5", hasConfirm, confirmButtonClicked);
                    }
                    else
                    {
                        _logger.LogWarning("[Playwright] Q6 Test Case 1 (Confirm Dialog) FAIL: No confirm dialog detected. hasConfirm={HasConfirm}, confirmButtonClicked={Clicked}", hasConfirm, confirmButtonClicked);
                    }
                    
                    _logger.LogInformation("[Playwright] Q6: Final confirm status - hasConfirm={HasConfirm}, confirmButtonClicked={Clicked}", hasConfirm, confirmButtonClicked);
                    await leftPage.WaitForTimeoutAsync(2000);

                    // Wait for navigation on left page
                    try
                    {
                        await leftPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                    }
                    catch { }

                    // Verify left page is on list page
                    var leftOnList = IsListPageUrl(leftPage.Url);
                    if (!leftOnList)
                    {
                        _logger.LogWarning("[Playwright] Q6: Left page not on list page after delete. Score set to 0.");
                        return Math.Max(0, score);
                    }

                    // Wait for left page to update
                    await leftPage.WaitForTimeoutAsync(2000);
                    try
                    {
                        await leftPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                    }
                    catch { }

                    // Verify panther is removed from left page first
                    var existsInLeftAfter = await VerifyPantherExistsInListWithPaginationAsync(leftPage, pantherName);
                    if (existsInLeftAfter)
                    {
                        deleteWithSignalRNote = "Fail test case: Realtime Update via SignalR - Panther still exists after delete";
                        _logger.LogWarning("[Playwright] Q6: Panther {Name} still exists in left page after delete. Test Case 2 (Realtime Update) FAIL. Score set to 0.", pantherName);
                        // Populate TestResultDetail before returning 0
                        if (_testResultDetail != null)
                        {
                            _testResultDetail.Q6DeleteWithSignalR = 0;
                            _testResultDetail.Q6DeleteWithSignalRNote = deleteWithSignalRNote;
                        }
                        return 0; // If realtime update fails, return 0 as per requirement
                    }

                    _logger.LogInformation("[Playwright] Q6: Panther {Name} removed from left page after delete", pantherName);

                    // Wait for SignalR update to propagate to right page (realtime update without reload)
                    // SignalR updates might take a few seconds
                    await rightPage.WaitForTimeoutAsync(4000);
                    
                    // Try to wait for table update (if SignalR updates DOM)
                    try
                    {
                        // Wait a bit more for any DOM updates
                        await rightPage.WaitForTimeoutAsync(2000);
                    }
                    catch { }

                    // Test Case 2: Realtime Update via SignalR (1.0 point)
                    // Verify panther is removed from right page (realtime update) - search in all pages
                    // Note: We check without reload to verify real SignalR update
                    var existsAfter = await VerifyPantherExistsInListWithPaginationAsync(rightPage, pantherName);
                    if (!existsAfter)
                    {
                        deleteWithSignalRScore = 1.5; // Both test cases passed (0.5 + 1.0)
                        score += 1.0;
                        _logger.LogInformation("[Playwright] Q6 Test Case 2 (Realtime Update) PASS: Panther {Name} removed from right page. Score: +1.0", pantherName);
                    }
                    else
                    {
                        deleteWithSignalRNote = "Fail test case: Realtime Update via SignalR - Panther still exists in right page after delete";
                        _logger.LogWarning("[Playwright] Q6 Test Case 2 (Realtime Update) FAIL: Panther {Name} still exists in right page. Score set to 0.", pantherName);
                        // Populate TestResultDetail before returning 0
                        if (_testResultDetail != null)
                        {
                            _testResultDetail.Q6DeleteWithSignalR = 0;
                            _testResultDetail.Q6DeleteWithSignalRNote = deleteWithSignalRNote;
                        }
                        return 0; // If realtime update fails, return 0 as per requirement
                    }
                }
                finally
                {
                    await rightPage.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[Playwright] Q6: Critical error during delete test: {Message}", ex.Message);
            }

            // Populate TestResultDetail
            if (_testResultDetail != null)
            {
                // Only set score if not already set (i.e., didn't return 0 early)
                if (deleteWithSignalRScore == 0.0 && score > 0)
                {
                    deleteWithSignalRScore = score;
                }
                _testResultDetail.Q6DeleteWithSignalR = deleteWithSignalRScore;
                _testResultDetail.Q6DeleteWithSignalRNote = deleteWithSignalRNote;
            }

            return Math.Min(1.5, Math.Max(0, score));
        }

        private async Task<ILocator?> FindDeleteButtonInCellAsync(ILocator cell)
        {
            var deleteSelectors = new[]
            {
                "button:has-text('Delete with SignalR')",
                "button:has-text('Delete with signalR')",
                "button:has-text('Delete with SingalR')",
                "a:has-text('Delete with SignalR')",
                "a:has-text('Delete with signalR')",
                "a:has-text('Delete with SingalR')",
                "button:has-text('Delete')",
                "a:has-text('Delete')",
                "button[title*='Delete' i]",
                "a[title*='Delete' i]",
                "[aria-label*='Delete' i]",
                "button:has([class*='delete' i])",
                "a:has([class*='delete' i])",
                "button[href*='Delete' i]",
                "a[href*='Delete' i]"
            };

            foreach (var selector in deleteSelectors)
            {
                try
                {
                    var locator = cell.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                    {
                        _logger.LogDebug("[Playwright] Q6: Found delete button using selector: {Selector} in cell", selector);
                        return locator;
                    }
                }
                catch { }
            }

            return null;
        }

        private async Task<ILocator?> FindDeleteButtonInRowAsync(ILocator row)
        {
            var deleteSelectors = new[]
            {
                "button:has-text('Delete with SignalR')",
                "button:has-text('Delete with signalR')",
                "button:has-text('Delete with SingalR')",
                "a:has-text('Delete with SignalR')",
                "a:has-text('Delete with signalR')",
                "a:has-text('Delete with SingalR')",
                "button:has-text('Delete')",
                "a:has-text('Delete')",
                "button[title*='Delete' i]",
                "a[title*='Delete' i]",
                "[aria-label*='Delete' i]",
                "button:has([class*='delete' i])",
                "a:has([class*='delete' i])",
                "button[href*='Delete' i]",
                "a[href*='Delete' i]"
            };

            foreach (var selector in deleteSelectors)
            {
                try
                {
                    var locator = row.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                    {
                        _logger.LogDebug("[Playwright] Q6: Found delete button using selector: {Selector} in row", selector);
                        return locator;
                    }
                }
                catch { }
            }

            return null;
        }

        private async Task<bool> CheckForConfirmDialogAsync(IPage page, string? dialogMessage)
        {
            // Check if dialog was captured
            if (!string.IsNullOrEmpty(dialogMessage))
            {
                var confirmTexts = new[]
                {
                    "Are you sure you want to delete this?",
                    "Are you sure you want to delete this Profile?",
                    "are you sure",
                    "confirm",
                    "delete",
                    "sure",
                    "want to delete",
                    "delete this",
                    "delete this Profile",
                    "Profile",
                    "this Profile"
                };

                foreach (var text in confirmTexts)
                {
                    if (dialogMessage.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("[Playwright] Q6: Confirm dialog detected via message text: {Text}", text);
                        return true;
                    }
                }
            }

            // Check for on-page confirm elements (modals, dialogs, etc.)
            var confirmSelectors = new[]
            {
                "text*='Are you sure'",
                "text*='are you sure'",
                "text*='Are you sure you want to delete this Profile'",
                "text*='are you sure you want to delete this profile'",
                "text*='delete this Profile'",
                "text*='delete this profile'",
                "text*='confirm'",
                "text*='Confirm'",
                "[role='alertdialog']",
                "[role='dialog']",
                ".modal:has-text('delete')",
                ".modal:has-text('Delete')",
                ".modal:has-text('Profile')",
                ".modal:has-text('profile')",
                ".modal-body:has-text('sure')",
                ".modal-body:has-text('confirm')",
                ".modal-body:has-text('Profile')",
                ".modal-body:has-text('profile')",
                "div:has-text('Are you sure')",
                "div:has-text('are you sure')",
                "div:has-text('delete this Profile')",
                "div:has-text('delete this profile')",
                "div:has-text('Profile')",
                "p:has-text('Are you sure')",
                "p:has-text('delete this Profile')",
                "span:has-text('Are you sure')",
                "span:has-text('delete this Profile')"
            };

            foreach (var selector in confirmSelectors)
            {
                try
                {
                    if (await page.Locator(selector).IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                    {
                        _logger.LogDebug("[Playwright] Q6: Confirm dialog detected via selector: {Selector}", selector);
                        return true;
                    }
                }
                catch { }
            }

            // Also check if confirm buttons are visible (indicates a confirm form/dialog exists)
            var confirmButtonSelectors = new[]
            {
                "button:has-text('Delete')",
                "button:has-text('Yes')",
                "button:has-text('OK')",
                "button:has-text('Confirm')",
                "input[type='submit'][value*='Delete' i]",
                "input[type='submit'][value*='Yes' i]"
            };

            foreach (var selector in confirmButtonSelectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                    {
                        // Check if this button is in a modal or dialog context
                        var parent = locator.Locator("xpath=ancestor::div[contains(@class,'modal') or contains(@role,'dialog')]");
                        var count = await parent.CountAsync();
                        if (count > 0)
                        {
                            _logger.LogDebug("[Playwright] Q6: Confirm dialog detected via confirm button in modal: {Selector}", selector);
                            return true;
                        }
                        
                        // Also check if page body contains confirm keywords (for form-based confirm pages)
                        var pageText = await page.Locator("body").InnerTextAsync();
                        var confirmKeywords = new[]
                        {
                            "Are you sure you want to delete this Profile",
                            "Are you sure you want to delete this",
                            "are you sure",
                            "delete this Profile",
                            "delete this profile",
                            "Profile",
                            "profile"
                        };
                        
                        bool hasConfirmKeyword = confirmKeywords.Any(keyword => 
                            pageText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                        
                        if (hasConfirmKeyword)
                        {
                            _logger.LogDebug("[Playwright] Q6: Confirm dialog detected via confirm button and page text keywords: {Selector}", selector);
                            return true;
                        }
                    }
                }
                catch { }
            }

            // Final check: search page body text for confirm keywords
            try
            {
                var pageText = await page.Locator("body").InnerTextAsync();
                var confirmKeywords = new[]
                {
                    "Are you sure you want to delete this Profile",
                    "Are you sure you want to delete this",
                    "are you sure",
                    "delete this Profile",
                    "delete this profile"
                };
                
                bool hasConfirmKeyword = confirmKeywords.Any(keyword => 
                    pageText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                
                if (hasConfirmKeyword)
                {
                    _logger.LogDebug("[Playwright] Q6: Confirm dialog detected via page body text keywords");
                    return true;
                }
            }
            catch { }

            return false;
        }

        private async Task<bool> AcceptConfirmDialogAsync(IPage page)
        {
            // Try to find and click confirm/delete/accept button on page
            var confirmButtonSelectors = new[]
            {
                "button:has-text('Delete')",
                "button:has-text('Yes')",
                "button:has-text('OK')",
                "button:has-text('Confirm')",
                "button:has-text('Accept')",
                "input[type='submit'][value*='Delete' i]",
                "input[type='submit'][value*='Yes' i]",
                "input[type='submit'][value*='OK' i]"
            };

            foreach (var selector in confirmButtonSelectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        _logger.LogInformation("[Playwright] Q6: Confirm button clicked: {Selector}", selector);
                        return true;
                    }
                }
                catch { }
            }
            
            return false;
        }

        private async Task<bool> VerifyPantherExistsInListAsync(IPage page, string pantherName)
        {
            try
            {
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    return false;
                }

                var headers = await page.Locator("table th").AllTextContentsAsync();
                var headerList = headers.ToList();
                var nameIndex = FindColumnIndex(headerList, "PantherName", "panthername", "Name", "name");

                if (nameIndex < 0)
                {
                    return false;
                }

                foreach (var row in rows)
                {
                    var cells = await row.Locator("td").AllAsync();
                    if (cells.Count > nameIndex)
                    {
                        var cellText = (await cells[nameIndex].TextContentAsync())?.Trim() ?? string.Empty;
                        if (cellText.Equals(pantherName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> VerifyPantherExistsInListWithPaginationAsync(IPage page, string pantherName)
        {
            try
            {
                // First, try current page
                if (await VerifyPantherExistsInListAsync(page, pantherName))
                {
                    return true;
                }

                // If not found, try navigating to page 1 (panther might be on first page)
                try
                {
                    var page1Selectors = new[]
                    {
                        "a:has-text('1')",
                        "button:has-text('1')",
                        "[aria-label*='page 1' i]",
                        ".pagination a:has-text('1')",
                        ".paging a:has-text('1')"
                    };

                    bool navigatedToPage1 = false;
                    foreach (var selector in page1Selectors)
                    {
                        try
                        {
                            var locator = page.Locator(selector).First;
                            if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                            {
                                await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                                await page.WaitForTimeoutAsync(1000);
                                try
                                {
                                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 2000 });
                                }
                                catch { }
                                navigatedToPage1 = true;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (navigatedToPage1)
                    {
                        if (await VerifyPantherExistsInListAsync(page, pantherName))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // If pagination navigation fails, continue
                }

                // Also try direct URL navigation to page 1
                try
                {
                    var currentUrl = page.Url;
                    var page1Url = currentUrl.Contains("?") 
                        ? currentUrl.Split('?')[0] + "?page=1"
                        : currentUrl + "?page=1";
                    
                    var response = await page.GotoAsync(page1Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                    if (response != null && response.Status < 400)
                    {
                        await page.WaitForTimeoutAsync(1500);
                        if (await VerifyPantherExistsInListAsync(page, pantherName))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // If direct navigation fails, continue
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> NavigateToLoginPageAsync(IPage page, string baseUrl, FlexibleElementFinder finder)
        {
            var baseCandidates = GetLoginBaseCandidates(baseUrl).ToList();
            var loginPaths = new[]
            {
                "",
                "/",
                "/Home",
                "/Home/Index",
                "/Account/Login",
                "/Login",
                "/Identity/Account/Login",
                "/Users/Login",
                "/PantherProfile/Index",
                "/PantherProfiles/Index"
            };

            foreach (var baseCandidate in baseCandidates)
            {
                foreach (var path in loginPaths)
                {
                    var targetUrl = CombineBaseAndPath(baseCandidate, path);
                    if (string.IsNullOrWhiteSpace(targetUrl))
                    {
                        continue;
                    }

                    if (await TryNavigateToLoginCandidateAsync(page, targetUrl, finder))
                    {
                        _logger.LogInformation("[Playwright] Q1: Using login URL {Url}", targetUrl);
                        return targetUrl;
                    }
                }
            }

            _logger.LogWarning("[Playwright] Q1: Could not positively identify login page. Continuing with base URL {BaseUrl}", baseUrl);
            var navigated = await NavigateWithSchemeFallbackAsync(page, baseUrl);
            return navigated ? baseUrl : string.Empty;
        }

        private IEnumerable<string> GetLoginBaseCandidates(string baseUrl)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                candidates.Add(baseUrl);
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                candidates.Add(uri.GetLeftPart(UriPartial.Authority));
                candidates.Add(uri.GetLeftPart(UriPartial.Path));
            }

            return candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate));
        }

        private async Task<bool> TryNavigateToLoginCandidateAsync(IPage page, string candidateUrl, FlexibleElementFinder finder)
        {
            try
            {
                var navigated = await NavigateWithSchemeFallbackAsync(page, candidateUrl);
                if (!navigated)
                {
                    return false;
                }
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                }
                catch
                {
                    // Ignore timeout
                }

                await page.WaitForTimeoutAsync(500);

                if (await finder.IsLoginPageAsync())
                {
                    _logger.LogInformation("[Playwright] Q1: Login page detected at {Url}", page.Url);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Playwright] Q1: Candidate login URL {Url} failed: {Message}", candidateUrl, ex.Message);
            }

            return false;
        }

        private static string CombineBaseAndPath(string baseUrl, string path)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return baseUrl;
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                if (Uri.TryCreate(baseUri, path, out var result))
                {
                    return result.ToString();
                }
            }

            if (!path.StartsWith("/"))
            {
                path = "/" + path;
            }

            return baseUrl.TrimEnd('/') + path;
        }

        private async Task<bool> NavigateWithSchemeFallbackAsync(IPage page, string baseUrl)
        {
            // Try original URL first
            try
            {
                var response = await page.GotoAsync(baseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 5000 });
                if (response != null && response.Status >= 400)
                {
                    throw new PlaywrightException($"HTTP status {response.Status} navigating to {baseUrl}");
                }
                _logger.LogInformation("[Playwright] Navigated OK: {Url}", baseUrl);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Navigate failed: {Url} → {Message}", baseUrl, ex.Message);

                // Flip scheme HTTP<->HTTPS and retry once
                var flipped = baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl.Replace("https://", "http://", StringComparison.OrdinalIgnoreCase)
                    : baseUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);

                try
                {
                    var response = await page.GotoAsync(flipped, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 5000 });
                    if (response != null && response.Status >= 400)
                    {
                        throw new PlaywrightException($"HTTP status {response.Status} navigating to {flipped}");
                    }
                    _logger.LogInformation("[Playwright] Navigated via fallback: {Url}", flipped);
                    return true;
                }
                catch (Exception ex2)
                {
                    _logger.LogError("[Playwright] Fallback navigate failed: {Url} → {Message}", flipped, ex2.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Navigate to login page by trying logout first if not already on login page
        /// </summary>
        private async Task<bool> NavigateToLoginPageOrLogoutAsync(IPage page, string loginUrl, FlexibleElementFinder finder)
        {
            try
            {
                // Wait a bit for page to stabilize
                await page.WaitForTimeoutAsync(500);
                
                // Check if already on login page
                var isLoginPage = await finder.IsLoginPageAsync();
                if (isLoginPage)
                {
                    _logger.LogInformation("[Playwright] Q1: Already on login page, no logout needed");
                    return true;
                }

                // Not on login page, try to find and click logout button
                _logger.LogInformation("[Playwright] Q1: Not on login page, attempting to logout");
                
                var logoutSelectors = new[]
                {
                    "a:has-text('Logout')",
                    "a:has-text('logout')",
                    "a:has-text('Đăng xuất')",
                    "a:has-text('đăng xuất')",
                    "button:has-text('Logout')",
                    "button:has-text('logout')",
                    "a[href*='/Account/Logout']",
                    "a[href*='/Identity/Account/Logout']",
                    "a[href*='/Users/Logout']",
                    "[role='button']:has-text('Logout')",
                    "[role='button']:has-text('logout')"
                };

                bool logoutClicked = false;
                foreach (var selector in logoutSelectors)
                {
                    try
                    {
                        var logoutLocator = page.Locator(selector).First;
                        var isVisible = await logoutLocator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 });
                        if (isVisible)
                        {
                            await logoutLocator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                            _logger.LogInformation("[Playwright] Q1: Logout button clicked using selector: {Selector}", selector);
                            logoutClicked = true;
                            
                            // Wait for navigation after logout
                            await page.WaitForTimeoutAsync(2000);
                            try
                            {
                                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                            }
                            catch
                            {
                                // Continue if network idle timeout
                            }
                            
                            // Check if we're now on login page (with retry)
                            for (int verifyRetry = 0; verifyRetry < 3; verifyRetry++)
                            {
                                await page.WaitForTimeoutAsync(500);
                                isLoginPage = await finder.IsLoginPageAsync();
                                if (isLoginPage)
                                {
                                    _logger.LogInformation("[Playwright] Q1: Successfully logged out and returned to login page");
                                    return true;
                                }
                                if (verifyRetry < 2)
                                {
                                    await page.WaitForTimeoutAsync(1000);
                                }
                            }
                            break;
                        }
                    }
                    catch
                    {
                        // Try next selector
                        continue;
                    }
                }

                if (!logoutClicked)
                {
                    _logger.LogWarning("[Playwright] Q1: Logout button not found, falling back to direct navigation");
                }
                else if (!isLoginPage)
                {
                    _logger.LogWarning("[Playwright] Q1: Logout clicked but not on login page yet, falling back to direct navigation");
                }

                // Fallback: navigate directly to login URL
                var fallbackSuccess = await NavigateWithSchemeFallbackAsync(page, loginUrl);
                if (fallbackSuccess)
                {
                    // Verify we're on login page after navigation
                    await page.WaitForTimeoutAsync(1500);
                    try
                    {
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 3000 });
                    }
                    catch
                    {
                        // Continue if network idle timeout
                    }
                    
                    isLoginPage = await finder.IsLoginPageAsync();
                    if (isLoginPage)
                    {
                        _logger.LogInformation("[Playwright] Q1: Successfully navigated to login page via fallback");
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("[Playwright] Q1: Navigated to {Url} but not detected as login page", page.Url);
                    }
                }
                return fallbackSuccess;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q1: Error in NavigateToLoginPageOrLogoutAsync: {Message}, falling back to direct navigation", ex.Message);
                return await NavigateWithSchemeFallbackAsync(page, loginUrl);
            }
        }

        private async Task<string> ResolveCanonicalBaseUrlAsync(IPage page, string inputBaseUrl)
        {
            var candidates = new List<string>();

            // Try input as-is first
            candidates.Add(inputBaseUrl);

            // Then try flipped scheme of input
            var flipped = inputBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? inputBaseUrl.Replace("https://", "http://", StringComparison.OrdinalIgnoreCase)
                : inputBaseUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(flipped, inputBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(flipped);
            }

            foreach (var candidate in candidates.Distinct())
            {
                try
                {
                    var response = await page.GotoAsync(candidate, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                    if (response != null && response.Status >= 400)
                    {
                        throw new PlaywrightException($"HTTP status {response.Status} navigating to {candidate}");
                    }
                    _logger.LogInformation("[Playwright] Canonical base resolved: {Url}", candidate);
                    return candidate;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] Probe failed: {Url} → {Message}", candidate, ex.Message);
                }
            }

            // As last resort, return the input (tests may fail, but we tried others)
            return inputBaseUrl;
        }

        private async Task<bool> NavigateToCreatePageAsync(IPage page, string canonicalBaseUrl)
        {
            // Quick check if already on create page
            if (IsCreatePageUrl(page.Url))
            {
                return true;
            }

            var createSelectors = new[]
            {
                "a:has-text('Create New')",
                "a:has-text('Create')",
                "button:has-text('Create')",
                "a[href*='/PantherProfile/Create']",
                "a[href*='/PantherProfiles/Create']"
            };

            // Try clicking create button/link with short timeout
            foreach (var selector in createSelectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                        await page.WaitForTimeoutAsync(1000);
                        // Quick check if we're on create page now
                        if (IsCreatePageUrl(page.Url))
                        {
                            _logger.LogInformation("[Playwright] Q3: Navigated to create page via selector {Selector}", selector);
                            return true;
                        }
                    }
                }
                catch
                {
                    // Try next selector
                }
            }

            // Try only 2 most common URLs with short timeout - if both fail, return false immediately
            var commonUrls = new[]
            {
                $"{canonicalBaseUrl}/PantherProfile/Create",
                $"{canonicalBaseUrl}/PantherProfiles/Create"
            };

            foreach (var targetUrl in commonUrls)
            {
                try
                {
                    var response = await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 2000 });
                    if (response != null && response.Status >= 400)
                    {
                        continue; // Try next URL
                    }
                    // Quick check if we're on create page
                    await page.WaitForTimeoutAsync(500);
                    if (IsCreatePageUrl(page.Url))
                    {
                        _logger.LogInformation("[Playwright] Q3: Navigated to create page via URL {Url}", targetUrl);
                        return true;
                    }
                }
                catch
                {
                    // Continue to try next URL
                }
            }

            _logger.LogWarning("[Playwright] Q3: Failed to navigate to create page via all attempted URLs");
            return false;
        }

        /// <summary>
        /// Check if URL is a create page (tries both PantherProfile and PantherProfiles)
        /// </summary>
        private static bool IsCreatePageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            
            return url.Contains("/PantherProfile/Create", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/PantherProfiles/Create", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panther/Create", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panthers/Create", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check if URL is a list page (tries both PantherProfile and PantherProfiles, but not Create)
        /// </summary>
        private static bool IsSearchPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            
            return url.Contains("/PantherProfile/Search", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/PantherProfiles/Search", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panther/Search", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panthers/Search", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsListPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            
            // Check for all possible panther list URL patterns
            var isPantherPage = url.Contains("/PantherProfile", StringComparison.OrdinalIgnoreCase) ||
                               url.Contains("/PantherProfiles", StringComparison.OrdinalIgnoreCase) ||
                               url.Contains("/Panther/", StringComparison.OrdinalIgnoreCase) ||
                               url.Contains("/Panthers/", StringComparison.OrdinalIgnoreCase) ||
                               url.EndsWith("/Panther", StringComparison.OrdinalIgnoreCase) ||
                               url.EndsWith("/Panthers", StringComparison.OrdinalIgnoreCase);
            
            if (!isPantherPage)
                return false;
            
            // Must not be create, edit, or search page
            return !IsCreatePageUrl(url) && !IsEditPageUrl(url) && !IsSearchPageUrl(url);
        }

        private async Task<bool> WaitForCreatePageAsync(IPage page)
        {
            try
            {
                // Try all possible URL patterns for create page
                var pattern1 = new Regex("/PantherProfiles?/Create", RegexOptions.IgnoreCase);
                var pattern2 = new Regex("/Panthers?/Create", RegexOptions.IgnoreCase);
                
                try
                {
                    await page.WaitForURLAsync(pattern1, new() { Timeout = 2000 });
                    return true;
                }
                catch
                {
                    try
                    {
                        await page.WaitForURLAsync(pattern2, new() { Timeout = 2000 });
                        return true;
                    }
                    catch
                    {
                        return IsCreatePageUrl(page.Url);
                    }
                }
            }
            catch
            {
                return IsCreatePageUrl(page.Url);
            }
        }

        /// <summary>
        /// Build create URLs for both PantherProfile and PantherProfiles variants
        /// </summary>
        private static IEnumerable<string> BuildCreateUrls(string currentUrl, string fallbackBaseUrl)
        {
            var candidates = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                // Try to find PantherProfile or PantherProfiles in current URL
                var markerIndex1 = currentUrl.LastIndexOf("/PantherProfile", StringComparison.OrdinalIgnoreCase);
                var markerIndex2 = currentUrl.LastIndexOf("/PantherProfiles", StringComparison.OrdinalIgnoreCase);
                
                if (markerIndex1 >= 0)
                {
                    var prefix = currentUrl.Substring(0, markerIndex1);
                    candidates.Add($"{prefix}/PantherProfile/Create");
                    candidates.Add($"{prefix}/PantherProfiles/Create");
                }
                else if (markerIndex2 >= 0)
                {
                    var prefix = currentUrl.Substring(0, markerIndex2);
                    candidates.Add($"{prefix}/PantherProfiles/Create");
                    candidates.Add($"{prefix}/PantherProfile/Create");
                }
            }

            // Add fallback URLs
            var baseUrl = fallbackBaseUrl.TrimEnd('/');
            candidates.Add($"{baseUrl}/PantherProfile/Create");
            candidates.Add($"{baseUrl}/PantherProfiles/Create");

            return candidates.Distinct();
        }

        private async Task<bool> NavigateToListPageAsync(IPage page, string canonicalBaseUrl)
        {
            if (IsListPageUrl(page.Url))
            {
                return true;
            }

            var listSelectors = new[]
            {
                "a:has-text('Back to List')",
                "a:has-text('Back to list')",
                "a:has-text('Panther List')",
                "button:has-text('Back to List')",
                "a[href*='/PantherProfile']",
                "a[href*='/PantherProfiles']",
                "a[href*='/Panther']",
                "a[href*='/Panthers']"
            };

            foreach (var selector in listSelectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        await locator.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                        if (await WaitForListNavigationAsync(page, 2000))
                        {
                            _logger.LogInformation("[Playwright] Navigated to list via selector {Selector}", selector);
                            return true;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            foreach (var targetUrl in BuildListUrls(canonicalBaseUrl, page.Url))
            {
                try
                {
                    var response = await page.GotoAsync(targetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 3000 });
                    if (response != null && response.Status >= 400)
                    {
                        _logger.LogDebug("[Playwright] List navigation HTTP {Status} for {Url}", response.Status, targetUrl);
                        continue;
                    }

                    if (await WaitForListNavigationAsync(page, 4000))
                    {
                        _logger.LogInformation("[Playwright] Navigated to list via URL {Url}", targetUrl);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("[Playwright] List navigation failed for {Url}: {Message}", targetUrl, ex.Message);
                }
            }

            _logger.LogWarning("[Playwright] Unable to reach panther list.");
            return false;
        }

        private async Task<bool> NavigateToEditPageAsync(IPage page, string canonicalBaseUrl)
        {
            if (IsEditPageUrl(page.Url))
            {
                return true;
            }

            if (!await NavigateToListPageAsync(page, canonicalBaseUrl))
            {
                return false;
            }

            // Expanded selectors for Update/Edit buttons - more flexible
            var editSelectors = new[]
            {
                // Link selectors - exact text
                "a:has-text('Update')",
                "a:has-text('update')",
                "a:has-text('Edit')",
                "a:has-text('edit')",
                // Link selectors - href based
                "a[href*='/Edit']",
                "a[href*='/Update']",
                "a[href*='/edit']",
                "a[href*='/update']",
                "a[href*='Edit']",
                "a[href*='Update']",
                // Button selectors - exact text
                "button:has-text('Update')",
                "button:has-text('update')",
                "button:has-text('Edit')",
                "button:has-text('edit')",
                // Input submit buttons
                "input[type='submit'][value*='Update' i]",
                "input[type='submit'][value*='Edit' i]",
                "input[type='button'][value*='Update' i]",
                "input[type='button'][value*='Edit' i]",
                // Title/aria-label based
                "a[title*='Update' i]",
                "a[title*='Edit' i]",
                "button[title*='Update' i]",
                "button[title*='Edit' i]",
                "[aria-label*='Update' i]",
                "[aria-label*='Edit' i]",
                // Class-based
                "a[class*='update' i]",
                "a[class*='edit' i]",
                "button[class*='update' i]",
                "button[class*='edit' i]",
                // Data attribute based
                "a[data-action*='update' i]",
                "a[data-action*='edit' i]",
                "button[data-action*='update' i]",
                "button[data-action*='edit' i]",
                // Generic clickable with Update/Edit text
                "[role='button']:has-text('Update')",
                "[role='button']:has-text('Edit')",
                "span:has-text('Update')",
                "span:has-text('Edit')"
            };

            var rows = page.Locator("table tbody tr");
            var rowCount = await rows.CountAsync();
            if (rowCount > 0)
            {
                // Try only first row with most common selectors - only 1 attempt to avoid hanging
                var row = rows.First;
                var commonSelectors = new[]
                {
                    "a:has-text('Update')",
                    "a:has-text('Edit')",
                    "a[href*='/Edit']",
                    "a[href*='/Update']",
                    "button:has-text('Update')",
                    "button:has-text('Edit')"
                };
                
                // Try most common selectors in first row only - stop after first successful click attempt
                foreach (var selector in commonSelectors)
                {
                    try
                    {
                        var locator = row.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                        {
                            await locator.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                            await page.WaitForTimeoutAsync(1000);
                            if (await WaitForEditPageAsync(page))
                            {
                                _logger.LogInformation("[Playwright] Q4: Navigated to edit page via first row selector {Selector}", selector);
                                return true;
                            }
                            // If click didn't navigate to edit page, return false immediately (only 1 attempt allowed)
                            _logger.LogWarning("[Playwright] Q4: Clicked update/edit button but did not navigate to edit page. Returning false.");
                            return false;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            _logger.LogWarning("[Playwright] Q4: Edit/Update action not found in first row. Returning false.");
            return false;
        }

        private async Task<bool> EnsureEditPageAsync(IPage page, string canonicalBaseUrl)
        {
            if (IsEditPageUrl(page.Url))
            {
                return true;
            }

            return await NavigateToEditPageAsync(page, canonicalBaseUrl);
        }

        private async Task<bool> WaitForEditPageAsync(IPage page)
        {
            // Quick check if already on edit page
            await page.WaitForTimeoutAsync(500);
            if (IsEditPageUrl(page.Url))
            {
                return true;
            }
            
            // Check for both Edit and Update URLs with all naming variants
            var editPattern = new Regex("/(PantherProfiles?|Panthers?)/Edit", RegexOptions.IgnoreCase);
            var updatePattern = new Regex("/(PantherProfiles?|Panthers?)/Update", RegexOptions.IgnoreCase);
            try
            {
                // Try waiting for Edit pattern first
                try
                {
                    await page.WaitForURLAsync(editPattern, new() { Timeout = 1500 });
                    return true;
                }
                catch
                {
                    // If Edit pattern doesn't match, try Update pattern
                    await page.WaitForURLAsync(updatePattern, new() { Timeout = 1500 });
                    return true;
                }
            }
            catch
            {
                // Final check
                await page.WaitForTimeoutAsync(500);
                return IsEditPageUrl(page.Url);
            }
        }

        private static bool IsEditPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            // Check for both Edit and Update URLs with all naming variants
            return url.Contains("/PantherProfile/Edit", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/PantherProfiles/Edit", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panther/Edit", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panthers/Edit", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/PantherProfile/Update", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/PantherProfiles/Update", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panther/Update", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/Panthers/Update", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> BuildListUrls(string canonicalBaseUrl, string currentUrl)
        {
            var candidates = new List<string>();
            var baseCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(canonicalBaseUrl))
            {
                baseCandidates.Add(canonicalBaseUrl.TrimEnd('/'));
            }

            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                try
                {
                    var uri = new Uri(currentUrl);
                    baseCandidates.Add(uri.GetLeftPart(UriPartial.Authority));
                }
                catch
                {
                    baseCandidates.Add(currentUrl.TrimEnd('/'));
                }
            }

            // Build list of all possible URL variants for panther list page
            // Different projects may use different naming conventions
            foreach (var baseValue in baseCandidates)
            {
                // Most common variants
                candidates.Add($"{baseValue}/PantherProfile");
                candidates.Add($"{baseValue}/PantherProfile/Index");
                candidates.Add($"{baseValue}/PantherProfiles");
                candidates.Add($"{baseValue}/PantherProfiles/Index");
                
                // Shorter variants (some projects use these)
                candidates.Add($"{baseValue}/Panther");
                candidates.Add($"{baseValue}/Panther/Index");
                candidates.Add($"{baseValue}/Panthers");
                candidates.Add($"{baseValue}/Panthers/Index");
                
                // Lowercase variants
                candidates.Add($"{baseValue}/pantherprofile");
                candidates.Add($"{baseValue}/pantherprofile/index");
                candidates.Add($"{baseValue}/pantherprofiles");
                candidates.Add($"{baseValue}/pantherprofiles/index");
                candidates.Add($"{baseValue}/panther");
                candidates.Add($"{baseValue}/panther/index");
                candidates.Add($"{baseValue}/panthers");
                candidates.Add($"{baseValue}/panthers/index");
            }

            return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string FormatDateForInput(DateTime value)
        {
            return value.ToString("yyyy-MM-ddTHH:mm");
        }

        private static readonly string[] FatalPageIndicators =
        {
            "An error occurred while processing your request",
            "An unhandled exception occurred",
            "Server Error",
            "HTTP Error",
            "Request ID:",
            "Stack trace",
            "This localhost page can’t be found"
        };

        private async Task<bool> SubmitCreateFormAsync(
            IPage page,
            string canonicalBaseUrl,
            int pantherTypeIndex,
            string pantherName,
            string weight,
            string characteristics,
            string warning,
            string modifiedDate)
        {
            if (!IsCreatePageUrl(page.Url))
            {
                if (!await NavigateToCreatePageAsync(page, canonicalBaseUrl))
                {
                    return false;
                }
            }

            if (!await SelectPantherTypeOptionAsync(page, pantherTypeIndex))
            {
                    _logger.LogWarning("[Playwright] PantherType: Select element not found.");
                return false;
            }

            await FillFieldAsync(page, "PantherName", pantherName);
            await FillFieldAsync(page, "Weight", weight);
            await FillFieldAsync(page, "Characteristics", characteristics);
            await FillFieldAsync(page, "Warning", warning);
            await FillFieldAsync(page, "ModifiedDate", modifiedDate);

            var createButton = await FindCreateButtonAsync(page);
            try
            {
                await createButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Form: Create button click failed. {Message}", ex.Message);
                return false;
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 2000 });
            }
            catch
            {
                // ignore - page might not respond, continue anyway
            }

            if (!await CheckForFatalPageStateAsync(page))
            {
                _logger.LogWarning("[Playwright] Form: Fatal page state detected after create submission.");
                return false;
            }

            return true;
        }

        private async Task<bool> SubmitUpdateFormAsync(
            IPage page,
            string canonicalBaseUrl,
            int pantherTypeIndex,
            string pantherName,
            string weight,
            string characteristics,
            string warning,
            string modifiedDate)
        {
            if (!await EnsureEditPageAsync(page, canonicalBaseUrl))
            {
                return false;
            }

            if (!await SelectPantherTypeOptionAsync(page, pantherTypeIndex))
            {
                _logger.LogWarning("[Playwright] Q4: PantherType select element not found while updating.");
                return false;
            }

            await FillFieldAsync(page, "PantherName", pantherName);
            await FillFieldAsync(page, "Weight", weight);
            await FillFieldAsync(page, "Characteristics", characteristics);
            await FillFieldAsync(page, "Warning", warning);
            // Skip ModifiedDate in update test case (Q4) - not required
            // await FillFieldAsync(page, "ModifiedDate", modifiedDate);

            var saveButton = await FindSaveButtonAsync(page);
            try
            {
                await saveButton.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Q4: Save button click failed. {Message}", ex.Message);
                return false;
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 2000 });
            }
            catch
            {
                // ignore - page might not respond, continue anyway
            }

            if (!await CheckForFatalPageStateAsync(page))
            {
                _logger.LogWarning("[Playwright] Q4: Fatal page state detected after update submission.");
                return false;
            }

            return true;
        }

        private async Task FillFieldAsync(IPage page, string fieldName, string value)
        {
            // Special handling for ModifiedDate with date picker
            if (fieldName.Equals("ModifiedDate", StringComparison.OrdinalIgnoreCase))
            {
                await FillModifiedDateAsync(page);
                return;
            }

            var selectors = GenerateFieldSelectors(fieldName).ToArray();
            bool fieldFound = false;

            // Try each selector individually to find the field
            foreach (var selector in selectors)
            {
                try
                {
                    var locator = page.Locator(selector).First;
                    // Use shorter timeout for each individual selector
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        await locator.FillAsync(value ?? string.Empty);
                        fieldFound = true;
                        _logger.LogDebug("[Playwright] Form: Successfully filled field {FieldName} using selector: {Selector}", fieldName, selector);
                        break;
                    }
                }
                catch
                {
                    // Try next selector
                    continue;
                }
            }

            // If still not found, try finding by label text (more flexible matching)
            if (!fieldFound)
            {
                try
                {
                    // Find label that contains the field name (case-insensitive, flexible matching)
                    var labelText = fieldName.Replace("_", " ").Replace(".", " ");
                    var labelSelectors = new[]
                    {
                        $"label:has-text('{fieldName}')",
                        $"label:has-text('{labelText}')",
                        $"label:has-text('{fieldName}' i)",
                        $"label:has-text('{labelText}' i)"
                    };

                    foreach (var labelSelector in labelSelectors)
                    {
                        try
                        {
                            var labels = await page.Locator(labelSelector).AllAsync();
                            foreach (var label in labels)
                            {
                                // Try to find input/textarea by 'for' attribute
                                var forAttr = await label.GetAttributeAsync("for");
                                if (!string.IsNullOrEmpty(forAttr))
                                {
                                    var inputByFor = page.Locator($"#{forAttr}").First;
                                    if (await inputByFor.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        await inputByFor.FillAsync(value ?? string.Empty);
                                        fieldFound = true;
                                        _logger.LogDebug("[Playwright] Form: Successfully filled field {FieldName} via label 'for' attribute", fieldName);
                                        break;
                                    }
                                }

                                // Try to find input/textarea as child or sibling of label
                                var inputInLabel = label.Locator("input, textarea").First;
                                if (await inputInLabel.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                {
                                    await inputInLabel.FillAsync(value ?? string.Empty);
                                    fieldFound = true;
                                    _logger.LogDebug("[Playwright] Form: Successfully filled field {FieldName} via label child", fieldName);
                                    break;
                                }

                                // Try adjacent sibling
                                var inputAfterLabel = page.Locator($"{labelSelector} + input, {labelSelector} + textarea").First;
                                if (await inputAfterLabel.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                {
                                    await inputAfterLabel.FillAsync(value ?? string.Empty);
                                    fieldFound = true;
                                    _logger.LogDebug("[Playwright] Form: Successfully filled field {FieldName} via label sibling", fieldName);
                                    break;
                                }
                            }
                            if (fieldFound) break;
                        }
                        catch { continue; }
                    }
                }
                catch { }
            }

            if (!fieldFound)
            {
                // For other fields, log warning but don't throw exception
                var selectorString = string.Join(", ", selectors.Take(5)); // Show first 5 selectors
                _logger.LogWarning("[Playwright] Form: Unable to fill field {FieldName}. Field not found with any selector. Tried {Count} selectors (showing first 5: {Selectors})", 
                    fieldName, selectors.Length, selectorString);
            }
        }

        private async Task FillModifiedDateAsync(IPage page)
        {
            try
            {
                _logger.LogInformation("[Playwright] Form: Attempting to fill ModifiedDate");

                // Step 1: Find ModifiedDate input field
                ILocator? dateInput = null;
                var dateInputSelectors = new[]
                {
                    "input[name*='ModifiedDate' i]",
                    "input[name*='Modified' i][name*='Date' i]",
                    "input[id*='ModifiedDate' i]",
                    "input[id*='Modified' i][id*='Date' i]",
                    "input[type='date']",
                    "input[type='datetime-local']",
                    "input[type='text'][placeholder*='date' i]",
                    "input[placeholder*='mm/dd/yyyy' i]",
                    "input[placeholder*='dd/mm/yyyy' i]"
                };

                foreach (var selector in dateInputSelectors)
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                        {
                            // Check if it's near a label with "Modified" or "Date"
                            var labelText = await locator.EvaluateAsync<string>("el => { const label = el.closest('label') || el.previousElementSibling; return label ? label.textContent || '' : ''; }");
                            if (string.IsNullOrEmpty(labelText))
                            {
                                // Try to find label by 'for' attribute or nearby
                                var id = await locator.GetAttributeAsync("id");
                                if (!string.IsNullOrEmpty(id))
                                {
                                    var labelByFor = page.Locator($"label[for='{id}']");
                                    if (await labelByFor.CountAsync() > 0)
                                    {
                                        labelText = await labelByFor.First.TextContentAsync() ?? "";
                                    }
                                }
                            }

                            if (labelText.IndexOf("Modified", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                labelText.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                string.IsNullOrEmpty(labelText)) // If no label, assume it's the right field
                            {
                                dateInput = locator;
                                _logger.LogInformation("[Playwright] Form: Found ModifiedDate input using selector: {Selector}", selector);
                                break;
                            }
                        }
                    }
                    catch { continue; }
                }

                // If not found by selectors, try finding by label
                if (dateInput == null)
                {
                    var labelSelectors = new[]
                    {
                        "label:has-text('ModifiedDate' i)",
                        "label:has-text('Modified Date' i)",
                        "label:has-text('Modified' i):has-text('Date' i)"
                    };

                    foreach (var labelSelector in labelSelectors)
                    {
                        try
                        {
                            var labels = await page.Locator(labelSelector).AllAsync();
                            foreach (var label in labels)
                            {
                                var forAttr = await label.GetAttributeAsync("for");
                                if (!string.IsNullOrEmpty(forAttr))
                                {
                                    var inputByFor = page.Locator($"#{forAttr}").First;
                                    if (await inputByFor.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                    {
                                        dateInput = inputByFor;
                                        _logger.LogInformation("[Playwright] Form: Found ModifiedDate input via label 'for' attribute");
                                        break;
                                    }
                                }

                                var inputInLabel = label.Locator("input").First;
                                if (await inputInLabel.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                {
                                    dateInput = inputInLabel;
                                    _logger.LogInformation("[Playwright] Form: Found ModifiedDate input in label");
                                    break;
                                }
                            }
                            if (dateInput != null) break;
                        }
                        catch { continue; }
                    }
                }

                if (dateInput == null)
                {
                    _logger.LogInformation("[Playwright] Form: ModifiedDate input not found, skipping (optional field)");
                    return;
                }

                // Step 2: Detect date format from placeholder, type, or pattern
                var inputType = await dateInput.GetAttributeAsync("type") ?? "";
                var placeholder = await dateInput.GetAttributeAsync("placeholder") ?? "";
                var pattern = await dateInput.GetAttributeAsync("pattern") ?? "";
                
                _logger.LogInformation("[Playwright] Form: ModifiedDate input type: {Type}, placeholder: {Placeholder}, pattern: {Pattern}", 
                    inputType, placeholder, pattern);

                // Determine format and generate date value
                string dateValue = "";
                
                // Check for datetime-local type
                if (inputType.Equals("datetime-local", StringComparison.OrdinalIgnoreCase))
                {
                    dateValue = "2025-10-13T10:10:10";
                    _logger.LogInformation("[Playwright] Form: Detected datetime-local format, using: {Value}", dateValue);
                }
                // Check for date type
                else if (inputType.Equals("date", StringComparison.OrdinalIgnoreCase))
                {
                    dateValue = "2025-10-13";
                    _logger.LogInformation("[Playwright] Form: Detected date format, using: {Value}", dateValue);
                }
                // Check placeholder for format hints
                else if (!string.IsNullOrEmpty(placeholder))
                {
                    var placeholderLower = placeholder.ToLowerInvariant();
                    
                    // MM/dd/yyyy or mm/dd/yyyy with time
                    if (placeholderLower.Contains("mm/dd/yyyy"))
                    {
                        if (placeholderLower.Contains("--:--") || placeholderLower.Contains("hh:mm") || placeholderLower.Contains("time"))
                        {
                            dateValue = "10/13/2025 10:10:10";
                            _logger.LogInformation("[Playwright] Form: Detected MM/dd/yyyy with time format, using: {Value}", dateValue);
                        }
                        else
                        {
                            dateValue = "10/13/2025";
                            _logger.LogInformation("[Playwright] Form: Detected MM/dd/yyyy format, using: {Value}", dateValue);
                        }
                    }
                    // dd/MM/yyyy with time
                    else if (placeholderLower.Contains("dd/mm/yyyy"))
                    {
                        if (placeholderLower.Contains("--:--") || placeholderLower.Contains("hh:mm") || placeholderLower.Contains("time"))
                        {
                            dateValue = "13/10/2025 10:10:10";
                            _logger.LogInformation("[Playwright] Form: Detected dd/MM/yyyy with time format, using: {Value}", dateValue);
                        }
                        else
                        {
                            dateValue = "13/10/2025";
                            _logger.LogInformation("[Playwright] Form: Detected dd/MM/yyyy format, using: {Value}", dateValue);
                        }
                    }
                    // yyyy/mm/dd or yyyy-MM-dd with time
                    else if (placeholderLower.Contains("yyyy/mm/dd") || placeholderLower.Contains("yyyy-mm-dd"))
                    {
                        if (placeholderLower.Contains("--:--") || placeholderLower.Contains("hh:mm") || placeholderLower.Contains("time"))
                        {
                            dateValue = "2025/10/13 10:10:10";
                            _logger.LogInformation("[Playwright] Form: Detected yyyy/mm/dd with time format, using: {Value}", dateValue);
                        }
                        else
                        {
                            dateValue = "2025/10/13";
                            _logger.LogInformation("[Playwright] Form: Detected yyyy/mm/dd format, using: {Value}", dateValue);
                        }
                    }
                    // Generic date with time
                    else if (placeholderLower.Contains("date") && (placeholderLower.Contains("time") || placeholderLower.Contains("--:--") || placeholderLower.Contains("hh:mm")))
                    {
                        // Default to MM/dd/yyyy with time
                        dateValue = "10/13/2025 10:10:10";
                        _logger.LogInformation("[Playwright] Form: Detected generic date with time format, using: {Value}", dateValue);
                    }
                }
                // Check pattern attribute
                else if (!string.IsNullOrEmpty(pattern))
                {
                    var patternLower = pattern.ToLowerInvariant();
                    if (patternLower.Contains("mm") && patternLower.Contains("dd") && patternLower.Contains("yyyy"))
                    {
                        if (patternLower.Contains("hh") || patternLower.Contains("mm") && patternLower.Count(c => c == 'm') > 2)
                        {
                            dateValue = "10/13/2025 10:10:10";
                            _logger.LogInformation("[Playwright] Form: Detected pattern with time format, using: {Value}", dateValue);
                        }
                        else
                        {
                            dateValue = "10/13/2025";
                            _logger.LogInformation("[Playwright] Form: Detected pattern format, using: {Value}", dateValue);
                        }
                    }
                }
                
                // If format not detected, try to fill directly and use date picker as fallback
                if (string.IsNullOrEmpty(dateValue))
                {
                    _logger.LogInformation("[Playwright] Form: Could not detect date format, trying date picker method");
                    // Continue to date picker logic below
                }
                else
                {
                    // Try to fill directly
                    try
                    {
                        await dateInput.FillAsync(dateValue);
                        await page.WaitForTimeoutAsync(300);
                        
                        // Verify the value was set
                        var filledValue = await dateInput.InputValueAsync();
                        if (!string.IsNullOrWhiteSpace(filledValue))
                        {
                            _logger.LogInformation("[Playwright] Form: ModifiedDate successfully filled with value: {Value}", filledValue);
                            return;
                        }
                        else
                        {
                            _logger.LogWarning("[Playwright] Form: ModifiedDate input is empty after fill, trying date picker method");
                            // Continue to date picker logic as fallback
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[Playwright] Form: Failed to fill ModifiedDate directly: {Message}, trying date picker method", ex.Message);
                        // Continue to date picker logic as fallback
                    }
                }

                // Step 3: Fallback to date picker method if direct fill didn't work
                _logger.LogInformation("[Playwright] Form: Using date picker method as fallback");

                // Step 3.1: Find calendar icon/button near the input
                ILocator? calendarButton = null;
                
                // Try to find calendar icon/button near the input
                var calendarSelectors = new[]
                {
                    "button[aria-label*='calendar' i]",
                    "button[aria-label*='date' i]",
                    "button[title*='calendar' i]",
                    "button[title*='date' i]",
                    "button:has(svg)",
                    "span[class*='calendar' i]",
                    "span[class*='date' i]",
                    "i[class*='calendar' i]",
                    "i[class*='date' i]",
                    "[class*='calendar' i]",
                    "[class*='datepicker' i]",
                    "[class*='date-picker' i]",
                    "button:has-text('📅')",
                    "button:has-text('📆')"
                };

                // First, try to find calendar button/icon as sibling or in same container using JavaScript
                var inputElement = await dateInput.ElementHandleAsync();
                if (inputElement != null)
                {
                    // Try to find calendar button near the input (sibling, parent's child, etc.)
                    var calendarButtonScript = @"
                        (input) => {
                            // Check next sibling
                            let next = input.nextElementSibling;
                            if (next && (next.tagName === 'BUTTON' || next.tagName === 'SPAN' || next.tagName === 'I' || 
                                next.className?.toLowerCase().includes('calendar') || 
                                next.className?.toLowerCase().includes('date'))) {
                                return next.id || next.className || 'next-sibling';
                            }
                            // Check previous sibling
                            let prev = input.previousElementSibling;
                            if (prev && (prev.tagName === 'BUTTON' || prev.tagName === 'SPAN' || prev.tagName === 'I' || 
                                prev.className?.toLowerCase().includes('calendar') || 
                                prev.className?.toLowerCase().includes('date'))) {
                                return prev.id || prev.className || 'prev-sibling';
                            }
                            // Check parent's children
                            let parent = input.parentElement;
                            if (parent) {
                                let buttons = parent.querySelectorAll('button, span, i, [class*=""calendar""], [class*=""date""]');
                                for (let btn of buttons) {
                                    if (btn !== input && (
                                        btn.className?.toLowerCase().includes('calendar') ||
                                        btn.className?.toLowerCase().includes('date') ||
                                        btn.getAttribute('aria-label')?.toLowerCase().includes('calendar') ||
                                        btn.getAttribute('aria-label')?.toLowerCase().includes('date') ||
                                        btn.getAttribute('title')?.toLowerCase().includes('calendar') ||
                                        btn.getAttribute('title')?.toLowerCase().includes('date')
                                    )) {
                                        return btn.id || btn.className || 'parent-child';
                                    }
                                }
                            }
                            return null;
                        }";

                    try
                    {
                        var calendarHint = await inputElement.EvaluateAsync<string>(calendarButtonScript);
                        if (!string.IsNullOrEmpty(calendarHint) && calendarHint != "null")
                        {
                            _logger.LogInformation("[Playwright] Form: Found calendar button hint near input: {Hint}", calendarHint);
                            // Try to find by the hint (id or class)
                            if (calendarHint.StartsWith("#") || !calendarHint.Contains(" "))
                            {
                                var byId = page.Locator($"#{calendarHint}").First;
                                if (await byId.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                {
                                    calendarButton = byId;
                                }
                            }
                            else
                            {
                                // Try by class
                                var classes = calendarHint.Split(' ');
                                foreach (var cls in classes)
                                {
                                    if (cls.Contains("calendar") || cls.Contains("date"))
                                    {
                                        var byClass = page.Locator($".{cls}").First;
                                        if (await byClass.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                        {
                                            calendarButton = byClass;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("[Playwright] Form: Could not find calendar button via JavaScript: {Message}", ex.Message);
                    }
                }

                // Try direct selectors near the input
                foreach (var selector in calendarSelectors)
                {
                    try
                    {
                        var locator = page.Locator(selector).First;
                        if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            calendarButton = locator;
                            _logger.LogInformation("[Playwright] Form: Found calendar button using selector: {Selector}", selector);
                            break;
                        }
                    }
                    catch { continue; }
                }

                // Step 3.2: Click calendar button if found, otherwise try clicking input directly
                if (calendarButton != null)
                {
                    try
                    {
                        await calendarButton.ClickAsync();
                        _logger.LogInformation("[Playwright] Form: Clicked calendar button");
                        await page.WaitForTimeoutAsync(500); // Wait for date picker to open
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[Playwright] Form: Failed to click calendar button: {Message}", ex.Message);
                    }
                }
                else
                {
                    // Try clicking the input field itself to open date picker
                    try
                    {
                        await dateInput.ClickAsync();
                        _logger.LogInformation("[Playwright] Form: Clicked ModifiedDate input to open date picker");
                        await page.WaitForTimeoutAsync(500); // Wait for date picker to open
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("[Playwright] Form: Failed to click ModifiedDate input: {Message}", ex.Message);
                    }
                }

                // Step 3.3: Wait for date picker to fully open
                await page.WaitForTimeoutAsync(500); // Wait for animation/rendering

                // Step 3.4: Try clicking "Today" button first (most reliable)
                var todayButtonSelectors = new[]
                {
                    "button:has-text('Today' i)",
                    "a:has-text('Today' i)",
                    "[role='button']:has-text('Today' i)",
                    ".today",
                    "[class*='today' i]",
                    "button[aria-label*='today' i]",
                    "a[aria-label*='today' i]"
                };

                bool todayClicked = false;
                foreach (var selector in todayButtonSelectors)
                {
                    try
                    {
                        var todayButton = page.Locator(selector).First;
                        if (await todayButton.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                        {
                            await todayButton.ClickAsync();
                            todayClicked = true;
                            _logger.LogInformation("[Playwright] Form: Clicked 'Today' button in date picker using selector: {Selector}", selector);
                            await page.WaitForTimeoutAsync(500); // Wait for date to be set
                            break;
                        }
                    }
                    catch { continue; }
                }

                // Step 3.5: If "Today" button not found, try to find and click a date cell
                bool dateSelected = false;
                if (!todayClicked)
                {
                    // Wait a bit more for date picker to render
                    await page.WaitForTimeoutAsync(300);

                    var datePickerSelectors = new[]
                    {
                        // Try table cells first (most common)
                        "table td:not([class*='disabled' i]):not([class*='old' i]):not([class*='new' i])",
                        "table td:not(.disabled):not(.old):not(.new)",
                        "table td.day:not(.disabled)",
                        "table td a:not([class*='disabled' i])",
                        "table td button:not([disabled])",
                        // Try with role attributes
                        "[role='gridcell']:not([aria-disabled='true'])",
                        "[role='gridcell'] button:not([disabled])",
                        "[role='gridcell'] a:not([aria-disabled='true'])",
                        // Try common date picker classes
                        ".datepicker td:not(.disabled):not(.old):not(.new)",
                        ".datepicker td.day:not(.disabled)",
                        ".datepicker td:not(.disabled) a",
                        ".day:not(.disabled)",
                        ".calendar-day:not(.disabled)",
                        // Try button/links with day-related text
                        "button[aria-label*='day' i]:not([disabled])",
                        "a[aria-label*='day' i]:not([aria-disabled='true'])",
                        // Bootstrap datepicker
                        ".bootstrap-datetimepicker-widget td:not(.disabled)",
                        // jQuery UI datepicker
                        ".ui-datepicker-calendar td:not(.ui-datepicker-other-month) a",
                        // Flatpickr
                        ".flatpickr-day:not(.flatpickr-disabled)",
                        // Material datepicker
                        ".mat-calendar-body-cell:not(.mat-calendar-body-disabled)",
                        // Generic clickable elements in calendar
                        "td:has(button):not([class*='disabled' i])",
                        "td:has(a):not([class*='disabled' i])",
                        "[class*='calendar' i] td:not([class*='disabled' i])",
                        "[class*='datepicker' i] td:not([class*='disabled' i])"
                    };

                    foreach (var selector in datePickerSelectors)
                    {
                        try
                        {
                            var dateCells = page.Locator(selector);
                            var count = await dateCells.CountAsync();
                            if (count > 0)
                            {
                                _logger.LogInformation("[Playwright] Form: Found {Count} date cells using selector: {Selector}", count, selector);
                                
                                // Try multiple dates (first, middle, last) to find one that works
                                var datesToTry = new[] { 0, count / 2, count - 1 };
                                foreach (var index in datesToTry)
                                {
                                    if (index < 0 || index >= count) continue;
                                    
                                    try
                                    {
                                        var dateToClick = dateCells.Nth(index);
                                        if (await dateToClick.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                                        {
                                            // Check if it's actually clickable (not disabled)
                                            var isDisabled = await dateToClick.EvaluateAsync<bool>("el => el.classList.contains('disabled') || el.hasAttribute('disabled') || el.closest('td')?.classList.contains('disabled')");
                                            if (isDisabled) continue;

                                            // Try to click the cell itself or a button/link inside it
                                            var clickableElement = dateToClick.Locator("button, a").First;
                                            var hasClickableChild = await clickableElement.CountAsync() > 0;
                                            
                                            if (hasClickableChild && await clickableElement.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                            {
                                                await clickableElement.ClickAsync();
                                            }
                                            else
                                            {
                                                await dateToClick.ClickAsync();
                                            }
                                            
                                            dateSelected = true;
                                            _logger.LogInformation("[Playwright] Form: Selected date at index {Index} in date picker using selector: {Selector}", index, selector);
                                            await page.WaitForTimeoutAsync(500); // Wait for date to be set
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug("[Playwright] Form: Failed to click date at index {Index}: {Message}", index, ex.Message);
                                        continue;
                                    }
                                }
                                
                                if (dateSelected) break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("[Playwright] Form: Error with selector {Selector}: {Message}", selector, ex.Message);
                            continue;
                        }
                    }
                }

                // Step 3.6: If still not selected, try clicking any visible date number
                if (!dateSelected && !todayClicked)
                {
                    try
                    {
                        // Try to find any clickable element that looks like a date (numbers 1-31)
                        // Get all td, button, a elements and filter by text content using JavaScript
                        var allElements = page.Locator("td, button, a");
                        var count = await allElements.CountAsync();
                        var dateRegex = new Regex(@"^\s*([1-9]|[12][0-9]|3[01])\s*$");
                        
                        for (int i = 0; i < Math.Min(count, 50); i++) // Limit to first 50 elements
                        {
                            try
                            {
                                var element = allElements.Nth(i);
                                if (await element.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                                {
                                    var text = await element.TextContentAsync();
                                    if (!string.IsNullOrWhiteSpace(text) && dateRegex.IsMatch(text.Trim()))
                                    {
                                        await element.ClickAsync();
                                        dateSelected = true;
                                        _logger.LogInformation("[Playwright] Form: Selected date using fallback method (clicking date number: {Date})", text.Trim());
                                        await page.WaitForTimeoutAsync(500);
                                        break;
                                    }
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("[Playwright] Form: Fallback date selection failed: {Message}", ex.Message);
                    }
                }

                // Step 3.7: If still not selected, click outside to close (might auto-select today)
                if (!dateSelected && !todayClicked)
                {
                    _logger.LogWarning("[Playwright] Form: Could not find clickable date in date picker, trying to click outside to close");
                    try
                    {
                        // Click on the input field itself or a safe area
                        await dateInput.ClickAsync();
                        await page.WaitForTimeoutAsync(300);
                        // Then click outside
                        await page.Mouse.ClickAsync(10, 10); // Click at top-left corner
                        await page.WaitForTimeoutAsync(300);
                    }
                    catch { }
                }

                // Step 3.8: Verify the date was filled
                var inputValue = await dateInput.InputValueAsync();
                if (!string.IsNullOrWhiteSpace(inputValue))
                {
                    _logger.LogInformation("[Playwright] Form: ModifiedDate successfully filled with value: {Value}", inputValue);
                }
                else
                {
                    _logger.LogInformation("[Playwright] Form: ModifiedDate input is empty after date picker interaction (may be optional)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Playwright] Form: Error filling ModifiedDate with date picker: {Message}", ex.Message);
            }
        }

        private async Task<ILocator> FindCreateButtonAsync(IPage page)
        {
            var createSelectors = new[]
            {
                "button:has-text('Create')",
                "input[type='submit'][value='Create']",
                "input[type='submit'][value*='Create' i]"
            };

            foreach (var selector in createSelectors)
            {
                var locator = page.Locator(selector).First;
                try
                {
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        return locator;
                    }
                }
                catch
                {
                    // try next selector
                }
            }

            throw new InvalidOperationException("Create button not found on the create page.");
        }

        private async Task<ILocator> FindSaveButtonAsync(IPage page)
        {
            var saveSelectors = new[]
            {
                "button:has-text('Save')",
                "button:has-text('save')",
                "button:has-text('Update')",
                "input[type='submit'][value*='Save' i]",
                "input[type='submit'][value*='Update' i]"
            };

            foreach (var selector in saveSelectors)
            {
                var locator = page.Locator(selector).First;
                try
                {
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        return locator;
                    }
                }
                catch
                {
                    continue;
                }
            }

            throw new InvalidOperationException("Save/Update button not found on the edit page.");
        }

        private async Task<bool> WaitForListNavigationAsync(IPage page, int timeoutMs = 2000)
        {
            try
            {
                // Try both PantherProfile and PantherProfiles patterns
                var pattern1 = new Regex("/PantherProfile/?$", RegexOptions.IgnoreCase);
                var pattern2 = new Regex("/PantherProfiles/?$", RegexOptions.IgnoreCase);
                
                try
                {
                    await page.WaitForURLAsync(pattern1, new() { Timeout = timeoutMs });
                    return true;
                }
                catch
                {
                    try
                    {
                        await page.WaitForURLAsync(pattern2, new() { Timeout = timeoutMs });
                        return true;
                    }
                    catch
                    {
                        return IsListPageUrl(page.Url);
                    }
                }
            }
            catch
            {
                return IsListPageUrl(page.Url);
            }
        }

        private async Task<bool> IsAnyValidationMessageVisibleAsync(IPage page, IEnumerable<string> messages)
        {
            foreach (var message in messages)
            {
                try
                {
                    var locator = page.GetByText(message, new() { Exact = false });
                    if (await locator.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 1000 }))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Continue checking other messages
                }
            }

            return false;
        }

        /// <summary>
        /// Check if any validation message contains keywords related to validation (characters, word, invalid, etc.)
        /// </summary>
        private async Task<bool> HasValidationMessageWithKeywordsAsync(IPage page, params string[] keywords)
        {
            var validationKeywords = keywords.Length > 0 ? keywords : new[] { "characters", "word", "invalid", "capital", "letter", "special" };
            
            _logger.LogDebug("[Playwright] Validation: Checking for keywords: {Keywords}", string.Join(", ", validationKeywords));
            
            // First, try to find error/validation elements
            var errorSelectors = new[]
            {
                "[class*='error']",
                "[class*='Error']",
                "[class*='validation']",
                "[class*='Validation']",
                "[role='alert']",
                "[role='alertdialog']",
                ".alert-danger",
                ".error-message",
                ".validation-summary-errors",
                ".field-validation-error",
                ".text-danger",
                "span[class*='error']",
                "span[class*='Error']",
                "div[class*='error']",
                "div[class*='Error']",
                "p[class*='error']",
                "p[class*='Error']"
            };

            foreach (var selector in errorSelectors)
            {
                try
                {
                    var errorElements = await page.Locator(selector).AllAsync();
                    foreach (var element in errorElements)
                    {
                        if (await element.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 }))
                        {
                            var errorText = await element.TextContentAsync() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(errorText))
                            {
                                _logger.LogDebug("[Playwright] Validation: Found element with text: {Text}", errorText);
                                if (validationKeywords.Any(keyword => errorText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _logger.LogDebug("[Playwright] Validation message found in element: {Text}", errorText);
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch { continue; }
            }

            // If not found via selectors, check page body text
            try
            {
                var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 }) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    _logger.LogDebug("[Playwright] Validation: Checking body text (length: {Length})", bodyText.Length);
                    if (validationKeywords.Any(keyword => bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogDebug("[Playwright] Validation keyword found in body text");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Playwright] Validation: Error reading body text: {Message}", ex.Message);
            }

            _logger.LogDebug("[Playwright] Validation: No validation message found with keywords: {Keywords}", string.Join(", ", validationKeywords));
            return false;
        }

        private async Task<bool> IsNewPantherFirstAsync(IPage page, string expectedName)
        {
            try
            {
                var createSettings = _settings.PantherCreate ?? new PantherCreateTestSettings();
                var addOkData = createSettings.AddOk ?? new PantherFormData();
                var expectedWeight = string.IsNullOrWhiteSpace(addOkData.Weight) ? "91" : addOkData.Weight;
                var expectedTypeCandidates = new List<string>();
                var typeIndexValue = addOkData.PantherTypeIndex.ToString();
                if (!string.IsNullOrWhiteSpace(typeIndexValue))
                {
                    expectedTypeCandidates.Add(typeIndexValue);
                    if (PantherTypeOptionMap.TryGetValue(typeIndexValue, out var mappedLabel))
                    {
                        expectedTypeCandidates.Add(mappedLabel);
                    }
                }
                if (createSettings.ExpectedPantherTypeOptions != null &&
                    addOkData.PantherTypeIndex > 0 &&
                    addOkData.PantherTypeIndex <= createSettings.ExpectedPantherTypeOptions.Count)
                {
                    var configuredLabel = createSettings.ExpectedPantherTypeOptions[addOkData.PantherTypeIndex - 1];
                    expectedTypeCandidates.Add(configuredLabel);
                }

                // Wait for table to be ready
                await page.WaitForTimeoutAsync(500);
                
                var rows = await page.Locator("table tbody tr").AllAsync();
                if (rows.Count == 0)
                {
                    _logger.LogWarning("[Playwright] Display Top: No rows found in table");
                    return false;
                }

                // Get table headers to find Weight and PantherType column indices
                var headerRows = await page.Locator("table thead tr, table tr:first-child").AllAsync();
                var headerList = new List<string>();
                
                if (headerRows.Count > 0)
                {
                    var headerCells = await headerRows[0].Locator("th, td").AllAsync();
                    foreach (var cell in headerCells)
                    {
                        var headerText = (await cell.TextContentAsync())?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(headerText))
                        {
                            headerList.Add(headerText);
                        }
                    }
                }
                
                // If no headers found, try to get from first row (might be header row)
                if (headerList.Count == 0 && rows.Count > 0)
                {
                    var firstRowHeaderCells = await rows[0].Locator("td, th").AllAsync();
                    foreach (var cell in firstRowHeaderCells)
                    {
                        var cellText = (await cell.TextContentAsync())?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(cellText))
                        {
                            headerList.Add(cellText);
                        }
                    }
                }

                // Find Weight and PantherType column indices (flexible)
                var weightIndex = FindColumnIndex(headerList, "Weight", "weight");
                var typeIndex = FindColumnIndex(headerList, "PantherType", "TypeName", "Type", "panthertype", "typename", "type");
                
                if (weightIndex < 0 || typeIndex < 0)
                {
                    _logger.LogWarning("[Playwright] Display Top: Weight or PantherType column not found. Weight index: {WeightIndex}, Type index: {TypeIndex}, Headers: {Headers}", 
                        weightIndex, typeIndex, string.Join(", ", headerList));
                    return false;
                }

                // Check row 1 (index 0) and row 2 (index 1) in tbody
                // Because sometimes row 1 might be header row
                for (int rowIndex = 0; rowIndex < Math.Min(2, rows.Count); rowIndex++)
                {
                    var rowCells = await rows[rowIndex].Locator("td").AllAsync();
                    if (rowCells.Count == 0)
                    {
                        continue;
                    }

                    // Check Weight
                    bool hasWeight91 = false;
                    if (weightIndex < rowCells.Count)
                    {
                        var weightText = (await rowCells[weightIndex].TextContentAsync())?.Trim() ?? string.Empty;
                        hasWeight91 = weightText.Equals(expectedWeight, StringComparison.OrdinalIgnoreCase);
                    }

                    // Check PantherType against expected candidates
                    bool hasCorrectType = false;
                    if (typeIndex < rowCells.Count)
                    {
                        var typeText = (await rowCells[typeIndex].TextContentAsync())?.Trim() ?? string.Empty;
                        hasCorrectType = expectedTypeCandidates.Any(candidate =>
                            typeText.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                    }

                    if (hasWeight91 && hasCorrectType)
                    {
                        _logger.LogInformation("[Playwright] Display Top: Found newly created Panther (Weight={Weight}, Type matches expected) in row {RowIndex}", 
                            expectedWeight, rowIndex + 1);
                        return true;
                    }
                }

                _logger.LogWarning("[Playwright] Display Top: Newly created Panther (Weight={Weight}) not found in top rows with expected type", expectedWeight);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Playwright] Display Top: Unable to verify first row. {Message}", ex.Message);
                return false;
            }
        }

        private async Task<(bool ComboExists, IReadOnlyList<string> Options)> GetPantherTypeOptionsAsync(IPage page)
        {
            List<string>? fallbackOptions = null;

            var selectLocator = await FindPantherTypeSelectAsync(page);
            if (selectLocator != null)
            {
                try
                {
                    await selectLocator.ClickAsync(new LocatorClickOptions { Force = true });
                    await page.WaitForTimeoutAsync(100);
                }
                catch (PlaywrightException ex)
                {
                    _logger.LogDebug("[Playwright] PantherType: Combobox click failed: {Message}", ex.Message);
                }

                var optionTexts = await selectLocator.EvaluateAsync<string[]>(
                    @"(element) => {
                        const options = Array.from(element.options ?? []);
                        return options.map(o => {
                            const text = (o.textContent ?? '').trim();
                            if (text.length > 0) return text;
                            const label = (o.label ?? '').trim();
                            if (label.length > 0) return label;
                            const dataText = (o.getAttribute('data-text') ?? '').trim();
                            if (dataText.length > 0) return dataText;
                            return (o.value ?? '').trim();
                        });
                    }");

                var trimmed = optionTexts
                    .Select(o => (o ?? string.Empty).Trim())
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .ToList();

                if (trimmed.Count > 0)
                {
                    try
                    {
                        await page.Keyboard.PressAsync("Escape");
                    }
                    catch
                    {
                        // ignore closing failures
                    }
                }

                if (trimmed.Any(value => value.Any(char.IsLetter)))
                {
                    _logger.LogInformation("[Playwright] PantherType: Combobox options detected: {Options}", string.Join(", ", trimmed));
                    return (true, trimmed);
                }

                if (trimmed.Count > 0 && fallbackOptions == null)
                {
                    fallbackOptions = trimmed;
                }
            }

            var inputWithList = page.Locator("input[list]");
            if (await inputWithList.CountAsync() > 0)
            {
                var datalistId = await inputWithList.First.GetAttributeAsync("list");
                if (!string.IsNullOrEmpty(datalistId))
                {
                    var options = await page.Locator($"datalist#{datalistId} option").AllTextContentsAsync();
                    var trimmed = options.Select(o => (o ?? string.Empty).Trim())
                                         .Where(o => !string.IsNullOrWhiteSpace(o))
                                         .ToList();
                    if (trimmed.Count > 0)
                    {
                        if (trimmed.Any(value => value.Any(char.IsLetter)))
                        {
                            _logger.LogInformation("[Playwright] PantherType: Datalist options detected: {Options}", string.Join(", ", trimmed));
                            return (true, trimmed);
                        }

                        if (fallbackOptions == null)
                        {
                            fallbackOptions = trimmed;
                        }
                    }
                }

                fallbackOptions ??= new List<string>();
            }

            var roleOptions = await page.EvaluateAsync<string[]>(
                @"() => Array.from(document.querySelectorAll('[role=""option""], li[role=""presentation""] span, .dropdown-menu .dropdown-item'))
                    .map(o => (o.textContent ?? '').trim())
                    .filter(text => text.length > 0)");
            if (roleOptions.Length > 0)
            {
                if (roleOptions.Any(value => value.Any(char.IsLetter)))
                {
                    _logger.LogInformation("[Playwright] PantherType: Role-based options detected: {Options}", string.Join(", ", roleOptions));
                    return (true, roleOptions);
                }

                if (fallbackOptions == null)
                {
                    fallbackOptions = roleOptions.ToList();
                }
            }

            if (fallbackOptions != null)
            {
                _logger.LogWarning("[Playwright] PantherType: Fallback options used. Raw values: {Options}",
                    string.Join(", ", fallbackOptions));
                return (true, fallbackOptions);
            }

            return (false, Array.Empty<string>());
        }

        private async Task<bool> SelectPantherTypeOptionAsync(IPage page, int optionIndex)
        {
            // optionIndex represents the PantherType identifier value (1-based)
            var desiredValue = optionIndex.ToString();
            var preferredLabels = PantherTypeOptionMap.TryGetValue(desiredValue, out var mappedLabel)
                ? new[] { mappedLabel }
                : Array.Empty<string>();
            var fallbackIndex = optionIndex > 0 ? optionIndex - 1 : optionIndex;

            var select = await FindPantherTypeSelectAsync(page);
            if (select != null)
            {
                try
                {
                    var optionCount = await select.Locator("option").CountAsync();
                    if (optionCount == 0)
                    {
                        await page.WaitForTimeoutAsync(500);
                        optionCount = await select.Locator("option").CountAsync();
                    }

                    if (optionCount == 0)
                    {
                        _logger.LogWarning("[Playwright] PantherType: No options found in select");
                        return false;
                    }

                    // Detect option format (numeric vs text) to improve selection reliability
                    _logger.LogInformation("[Playwright] PantherType: Detecting option format for desired value {Value}", desiredValue);
                    var allOptions = await select.Locator("option").AllAsync();
                    var optionInfos = new List<(string text, string value, int index)>();

                    for (int i = 0; i < allOptions.Count; i++)
                    {
                        var option = allOptions[i];
                        var optionText = (await option.TextContentAsync() ?? string.Empty).Trim();
                        var optionValue = await option.GetAttributeAsync("value") ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(optionText) ||
                            optionText.Equals("--Select--", StringComparison.OrdinalIgnoreCase) ||
                            optionText.Equals("Select", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        optionInfos.Add((optionText, optionValue, i));
                    }

                    if (optionInfos.Count > 0)
                    {
                        bool areNumericOptions = optionInfos.Count(opt =>
                            int.TryParse(opt.value, out _) ||
                            int.TryParse(opt.text, out _)) > optionInfos.Count / 2;

                        _logger.LogInformation("[Playwright] PantherType: Options format detected - Numeric: {IsNumeric}, Total options: {Count}",
                            areNumericOptions, optionInfos.Count);

                        if (areNumericOptions)
                        {
                            foreach (var opt in optionInfos)
                            {
                                if (opt.value.Equals(desiredValue, StringComparison.OrdinalIgnoreCase) ||
                                    opt.text.Equals(desiredValue, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!string.IsNullOrEmpty(opt.value) &&
                                        await TrySelectOptionAsync(select, new SelectOptionValue { Value = opt.value }))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected option by numeric value '{Value}'", opt.value);
                                        return true;
                                    }

                                    if (await ForceSelectOptionViaScriptAsync(select, opt.index))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected numeric option via script at index {Index}", opt.index);
                                        return true;
                                    }

                                    if (await TrySelectOptionAsync(select, new SelectOptionValue { Index = opt.index }))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected numeric option by index {Index}", opt.index);
                                        return true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            var labelKeywords = preferredLabels.Length > 0
                                ? preferredLabels.Select(label => label.ToLowerInvariant()).ToArray()
                                : new[] { "black jaguars", "jaguars" };

                            _logger.LogInformation("[Playwright] PantherType: Selecting option with text containing any of: {Labels}", string.Join(", ", labelKeywords));

                            foreach (var opt in optionInfos)
                            {
                                var optTextLower = opt.text.ToLowerInvariant();
                                if (labelKeywords.Any(keyword => optTextLower.Contains(keyword)))
                                {
                                    if (await TrySelectOptionAsync(select, new SelectOptionValue { Label = opt.text }))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected option by text '{Text}'", opt.text);
                                        return true;
                                    }

                                    if (!string.IsNullOrEmpty(opt.value) &&
                                        await TrySelectOptionAsync(select, new SelectOptionValue { Value = opt.value }))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected option by value '{Value}' (text: '{Text}')", opt.value, opt.text);
                                        return true;
                                    }

                                    if (await ForceSelectOptionViaScriptAsync(select, opt.index))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected option via script at index {Index} with text '{Text}'", opt.index, opt.text);
                                        return true;
                                    }

                                    if (await TrySelectOptionAsync(select, new SelectOptionValue { Index = opt.index }))
                                    {
                                        _logger.LogInformation("[Playwright] PantherType: Selected option by index {Index} with text '{Text}'", opt.index, opt.text);
                                        return true;
                                    }
                                }
                            }
                        }
                    }

                    // Generic fallbacks
                    if (await ForceSelectOptionViaScriptAsync(select, fallbackIndex))
                    {
                        _logger.LogInformation("[Playwright] PantherType: Selected option via script at fallback index {Index}", fallbackIndex);
                        return true;
                    }

                    foreach (var label in preferredLabels)
                    {
                        if (await TrySelectOptionAsync(select, new SelectOptionValue { Label = label }))
                        {
                            _logger.LogInformation("[Playwright] PantherType: Selected option by label '{Label}'", label);
                            return true;
                        }
                    }

                    if (await TrySelectOptionAsync(select, new SelectOptionValue { Value = desiredValue }))
                    {
                        _logger.LogInformation("[Playwright] PantherType: Selected option by value '{Value}'", desiredValue);
                        return true;
                    }

                    if (await TrySelectOptionAsync(select, new SelectOptionValue { Index = fallbackIndex }))
                    {
                        _logger.LogInformation("[Playwright] PantherType: Selected option by fallback index {Index}", fallbackIndex);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[Playwright] PantherType: Error selecting option: {Message}", ex.Message);
                }
            }

            var datalistInput = page.Locator("input[list]");
            if (await datalistInput.CountAsync() > 0)
            {
                var firstInput = datalistInput.First;
                var datalistId = await firstInput.GetAttributeAsync("list");
                if (!string.IsNullOrEmpty(datalistId))
                {
                    var options = await page.Locator($"datalist#{datalistId} option").AllTextContentsAsync();
                    if (options.Count > optionIndex)
                    {
                        var target = options[optionIndex];
                        await firstInput.FillAsync(target);
                        _logger.LogInformation("[Playwright] PantherType: Selected via datalist value '{Value}'", target);
                        return true;
                    }
                }
            }

            return false;
        }

        private async Task<ILocator?> FindPantherTypeSelectAsync(IPage page)
        {
            foreach (var selector in PantherTypeSelectSelectors)
            {
                try
                {
                    var locator = page.Locator(selector);
                    if (await locator.CountAsync() > 0)
                    {
                        return locator.First;
                    }
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (PlaywrightException)
                {
                    continue;
                }
            }

            return null;
        }

        private async Task<bool> TrySelectOptionAsync(ILocator selectLocator, SelectOptionValue option)
        {
            try
            {
                // Giảm timeout xuống 500ms vì đã thử JavaScript trước
                await selectLocator.SelectOptionAsync(option, new LocatorSelectOptionOptions { Timeout = 500 });
                return true;
            }
            catch (PlaywrightException)
            {
                return false;
            }
        }

        private async Task<bool> ForceSelectOptionViaScriptAsync(ILocator selectLocator, int optionIndex)
        {
            try
            {
                var handle = await selectLocator.ElementHandleAsync();
                if (handle == null)
                {
                    return false;
                }

                var success = await handle.EvaluateAsync<bool>(
                    @"(element, index) => {
                        try {
                            const options = Array.from(element.options || []);
                            if (index < 0 || index >= options.length) {
                                return false;
                            }
                            const target = options[index];
                            if (!target) {
                                return false;
                            }
                            // Set value trực tiếp
                            element.selectedIndex = index;
                            element.value = target.value;
                            
                            // Trigger events để đảm bảo form validation hoạt động
                            element.dispatchEvent(new Event('input', { bubbles: true, cancelable: true }));
                            element.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
                            
                            // Verify selection
                            return element.selectedIndex === index && element.value === target.value;
                        } catch (e) {
                            return false;
                        }
                    }", 
                    optionIndex);
                
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Playwright] PantherType: Script-based selection failed: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Find column index by flexible name matching (case-insensitive, partial match)
        /// </summary>
        private static int FindColumnIndex(IReadOnlyList<string> headers, params string[] columnNameVariants)
        {
            for (int i = 0; i < headers.Count; i++)
            {
                foreach (var variant in columnNameVariants)
                {
                    if (headers[i].Contains(variant, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return -1;
        }

        private async Task<bool> CheckForFatalPageStateAsync(IPage page)
        {
            try
            {
                var bodyText = await page.Locator("body").InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 }) ?? string.Empty;
                foreach (var indicator in FatalPageIndicators)
                {
                    if (bodyText.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Playwright] Fatal page indicator detected: {Indicator}", indicator);
                        return false;
                    }
                }
            }
            catch
            {
                // If we cannot read the body, assume not fatal
            }

            return true;
        }

        private static IEnumerable<string> GenerateFieldSelectors(string baseFieldName)
        {
            // Standard name and id selectors
            yield return $"input[name='{baseFieldName}']";
            yield return $"input[id='{baseFieldName}']";
            yield return $"textarea[name='{baseFieldName}']";
            yield return $"textarea[id='{baseFieldName}']";

            // PantherProfile prefix variants
            yield return $"input[name='PantherProfile.{baseFieldName}']";
            yield return $"input[id='PantherProfile_{baseFieldName}']";
            yield return $"textarea[name='PantherProfile.{baseFieldName}']";
            yield return $"textarea[id='PantherProfile_{baseFieldName}']";

            // Label-based selectors (find input/textarea associated with label)
            // Note: Playwright doesn't support :near, so we use adjacent selectors
            yield return $"label:has-text('{baseFieldName}') + input";
            yield return $"label:has-text('{baseFieldName}') + textarea";
            yield return $"label:has-text('{baseFieldName}') ~ input";
            yield return $"label:has-text('{baseFieldName}') ~ textarea";
            // Also try finding input/textarea that has a label with the field name
            yield return $"input:has(+ label:has-text('{baseFieldName}'))";
            yield return $"textarea:has(+ label:has-text('{baseFieldName}'))";

            // Placeholder-based selectors
            yield return $"input[placeholder*='{baseFieldName}' i]";
            yield return $"textarea[placeholder*='{baseFieldName}' i]";

            // Type-specific selectors
            yield return $"input[type='text'][name*='{baseFieldName}' i]";
            yield return $"input[type='text'][id*='{baseFieldName}' i]";
            yield return $"input[type='number'][name*='{baseFieldName}' i]";
            yield return $"input[type='number'][id*='{baseFieldName}' i]";
            yield return $"input[type='date'][name*='{baseFieldName}' i]";
            yield return $"input[type='date'][id*='{baseFieldName}' i]";
            yield return $"input[type='datetime-local'][name*='{baseFieldName}' i]";
            yield return $"input[type='datetime-local'][id*='{baseFieldName}' i]";

            // Case-insensitive name/id variants
            yield return $"input[name*='{baseFieldName}' i]";
            yield return $"input[id*='{baseFieldName}' i]";
            yield return $"textarea[name*='{baseFieldName}' i]";
            yield return $"textarea[id*='{baseFieldName}' i]";

            // Data attribute selectors
            yield return $"input[data-field='{baseFieldName}']";
            yield return $"input[data-field*='{baseFieldName}' i]";
            yield return $"textarea[data-field='{baseFieldName}']";
            yield return $"textarea[data-field*='{baseFieldName}' i]";

            // Class-based selectors (common patterns)
            yield return $"input.{baseFieldName}";
            yield return $"textarea.{baseFieldName}";
            yield return $"input[class*='{baseFieldName}' i]";
            yield return $"textarea[class*='{baseFieldName}' i]";
        }

        private async Task EnsureBrowsersInstalledAsync(CancellationToken cancellationToken)
        {
            // Double-check pattern to avoid multiple installations
            if (_browsersInstalled)
            {
                _logger.LogInformation("[Playwright] Browsers already installed, skipping installation");
                return;
            }

            await _installSemaphore.WaitAsync(cancellationToken);
            try
            {
                // Check again after acquiring lock
                if (_browsersInstalled)
                {
                    _logger.LogInformation("[Playwright] Browsers already installed (checked in semaphore), skipping installation");
                    return;
                }

                _logger.LogInformation("[Playwright] Checking if browsers are installed...");
                
                // Check if browsers are already installed by looking for chromium executable
                var playwrightDriversPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ms-playwright");
                
                // Check if directory exists first
                if (Directory.Exists(playwrightDriversPath))
                {
                    var chromiumExists = Directory.GetDirectories(playwrightDriversPath, "chromium-*")
                        .Any(dir => File.Exists(Path.Combine(dir, "chrome-win", "chrome.exe")));

                    if (chromiumExists)
                    {
                        _logger.LogInformation("[Playwright] Browsers already installed at {Path}, skipping installation", playwrightDriversPath);
                        _browsersInstalled = true;
                        return;
                    }
                }

                _logger.LogWarning("[Playwright] Browsers not found. Starting installation (this may take 1-2 minutes)...");
                
                var installStartTime = DateTime.UtcNow;
                
                // Run installation with timeout
                var installTask = Task.Run(() =>
                {
                    Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                }, cancellationToken);
                
                // Wait with timeout (max 3 minutes for browser download)
                var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                try
                {
                    await installTask.WaitAsync(linkedCts.Token);
                    var duration = DateTime.UtcNow - installStartTime;
                    _logger.LogInformation("[Playwright] Browser installation completed successfully in {Duration} seconds", duration.TotalSeconds);
                    _browsersInstalled = true;
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogError("[Playwright] Browser installation timed out after 3 minutes");
                    throw new TimeoutException("Playwright browser installation timed out");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Playwright] Browser installation failed: {Message}", ex.Message);
                // Don't set _browsersInstalled = true on failure, so it will retry next time
                throw;
            }
            finally
            {
                _installSemaphore.Release();
            }
        }
    }
}




