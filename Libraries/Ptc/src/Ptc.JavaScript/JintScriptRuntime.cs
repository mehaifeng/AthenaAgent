using System.Text.Json;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Runtime;
using Ptc.Core;

namespace Ptc.JavaScript;

/// <summary>
/// One engine per run, with native Promise/TaskInterop carrying JSON strings only.
/// No engine or JsValue is touched by host I/O completion threads. This is not OS isolation.
/// </summary>
public sealed class JintScriptRuntime : IPtcScriptRuntime
{
    public async ValueTask RunAsync(string code, JsonElement inputs, IPtcScriptBridge bridge,
        PtcExecutionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        limits.Validate();
        if (code.Length > limits.MaxCodeChars)
            throw new PtcRuntimeException("invalid_code", "Program exceeds its character limit.", PtcRunStatus.Rejected);
        var userFunction = "(async function(tools, text, inputs) {\n\"use strict\";\n" + code + "\n})";
        try
        {
            // Structural wrapper validation, not an AST blacklist or a sandbox proof. Prevent a
            // purported function body from closing our wrapper and running before bridge hiding.
            var prepared = Engine.PrepareScript(userFunction);
            if (prepared.Program?.Body.Count != 1
                || prepared.Program.Body[0] is not ExpressionStatement { Expression: FunctionExpression { Async: true } })
                throw new PtcRuntimeException("invalid_code", "Expected an async function body.", PtcRunStatus.Rejected);
        }
        catch (ParseErrorException)
        { throw new PtcRuntimeException("invalid_code", "Program is not a valid JavaScript function body.", PtcRunStatus.Rejected); }
        catch (JavaScriptException)
        { throw new PtcRuntimeException("invalid_code", "Program is not a valid JavaScript function body.", PtcRunStatus.Rejected); }
        catch (ScriptPreparationException)
        { throw new PtcRuntimeException("invalid_code", "Program is not a valid JavaScript function body.", PtcRunStatus.Rejected); }

        using var engine = new Engine(options =>
        {
            options.Strict = true;
            options.Host.StringCompilationAllowed = false;
            options.Interop.Enabled = false;
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            options.Interop.AllowWrite = false;
            options.Interop.ExposeDetailedResolutionErrors = false;
            options.ExperimentalFeatures = ExperimentalFeature.TaskInterop;
            options.AgentCanSuspend = false;
            options.Json.MaxParseDepth = limits.MaxJsonDepth;
            options.Constraints.StackOverflowGuard = true;
            options.Constraints.MaxArraySize = (uint)Math.Min(limits.EngineMemoryBytes / 8, uint.MaxValue - 1);
            options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(Math.Min(limits.MaxWallTime.TotalMilliseconds, 1000));
            options.Constraints.PromiseTimeout = limits.MaxWallTime;
            options.MaxStatements((int)limits.MaxStatements);
            options.LimitRecursion(limits.MaxRecursionDepth);
            options.LimitMemory(limits.EngineMemoryBytes);
            options.CancellationToken(cancellationToken);
        });

        // These callbacks receive/return primitives only. TaskInterop converts Task<string> into
        // native promises, never a script-visible CLR Task or business object.
        engine.SetValue("__ptc_call", new Func<string, string, Task<string>>(async (name, json) =>
        {
            JsonElement arguments;
            try { arguments = PtcJson.Parse(json, limits.MaxArgumentsBytes, limits.MaxJsonDepth, requireObject: true); }
            catch (PtcRuntimeException) { arguments = default; } // Invalid attempts still pass the coordinator's accounting gate.
            try
            {
                var result = await bridge.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(result, PtcJson.Options);
            }
            catch (PtcRuntimeException ex)
            { return JsonSerializer.Serialize(PtcToolResult.Error(ex.Code, ex.Message), PtcJson.Options); }
            catch (OperationCanceledException)
            { return JsonSerializer.Serialize(PtcToolResult.Error("cancelled", "Execution was cancelled."), PtcJson.Options); }
        }));
        engine.SetValue("__ptc_emit", new Action<string>(json =>
        {
            // Parse is strict and the Core Emit gate owns the sticky output budget.
            var output = json.Length == 0 ? default : PtcJson.Parse(json, int.MaxValue, limits.MaxJsonDepth);
            bridge.Emit(output);
        }));
        engine.SetValue("__ptc_catalog", JsonSerializer.Serialize(bridge.Tools, PtcJson.Options));
        engine.SetValue("__ptc_inputs", inputs.GetRawText());
        engine.SetValue("__ptc_depth", limits.MaxJsonDepth);
        engine.SetValue("__ptc_args", limits.MaxArgumentsBytes);
        engine.SetValue("__ptc_output", limits.MaxOutputBytes);
        try
        {
            await engine.EvaluateAsync(Bootstrap + userFunction + ")", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PtcRuntimeException) { throw; }
        catch (StatementsCountOverflowException)
        { throw new PtcRuntimeException("statement_limit_exceeded", "JavaScript statement limit exceeded.", PtcRunStatus.BudgetExceeded); }
        catch (RecursionDepthOverflowException)
        { throw new PtcRuntimeException("recursion_limit_exceeded", "JavaScript recursion limit exceeded.", PtcRunStatus.BudgetExceeded); }
        catch (MemoryLimitExceededException)
        { throw new PtcRuntimeException("engine_memory_limit_exceeded", "JavaScript allocation limit exceeded.", PtcRunStatus.BudgetExceeded); }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (TimeoutException)
        { throw new PtcRuntimeException("deadline_exceeded", "JavaScript promise deadline elapsed.", PtcRunStatus.TimedOut); }
        catch (ParseErrorException)
        { throw new PtcRuntimeException("invalid_code", "Program is not valid JavaScript.", PtcRunStatus.Rejected); }
        catch (Exception)
        {
            // Neither Jint/CLR stacks nor potentially secret host exception text is model output.
            throw new PtcRuntimeException("script_error", "JavaScript execution failed; check the function body and JSON values.");
        }
    }

    private const string Bootstrap = """
        (function(program) {
          "use strict";
          const call = globalThis.__ptc_call, emit = globalThis.__ptc_emit;
          const parse = JSON.parse.bind(JSON), quote = JSON.stringify.bind(JSON);
          const catalog = parse(globalThis.__ptc_catalog), inputs = parse(globalThis.__ptc_inputs);
          const depthLimit = globalThis.__ptc_depth, argsLimit = globalThis.__ptc_args, outputLimit = globalThis.__ptc_output;
          const ownKeys = Reflect.ownKeys, descriptor = Object.getOwnPropertyDescriptor;
          const prototype = Object.getPrototypeOf, plain = Object.prototype;
          const isArray = Array.isArray, finite = Number.isFinite, integer = Number.isInteger, safe = Number.isSafeInteger;
          const freeze = Object.freeze, TypeErrorCtor = TypeError;
          const create = Object.create, has = Function.call.bind(Object.prototype.hasOwnProperty);
          delete globalThis.__ptc_call; delete globalThis.__ptc_emit;
          delete globalThis.__ptc_catalog; delete globalThis.__ptc_inputs;
          delete globalThis.__ptc_depth; delete globalThis.__ptc_args; delete globalThis.__ptc_output;

          function encode(value, limit) {
            const seen = [];
            let size = 0;
            function add(text) {
              size += text.length;
              if (size > limit) throw new TypeErrorCtor("JSON value exceeds its size limit");
              return text;
            }
            function visit(item, depth) {
              if (depth > depthLimit) throw new TypeErrorCtor("JSON nesting limit exceeded");
              if (item === null) return add("null");
              const type = typeof item;
              if (type === "string") {
                // JSON.stringify would otherwise preserve lone surrogate code units as lossy data.
                for (let i = 0; i < item.length; i++) {
                  const c = item.charCodeAt(i);
                  if (c >= 0xD800 && c <= 0xDBFF) {
                    const d = item.charCodeAt(++i);
                    if (!(d >= 0xDC00 && d <= 0xDFFF)) throw new TypeErrorCtor("Invalid Unicode string");
                  } else if (c >= 0xDC00 && c <= 0xDFFF) throw new TypeErrorCtor("Invalid Unicode string");
                }
                return add(quote(item));
              }
              if (type === "boolean") return add(item ? "true" : "false");
              if (type === "number") {
                if (!finite(item) || (integer(item) && !safe(item))) throw new TypeErrorCtor("Invalid or unsafe JSON number");
                return add(quote(item));
              }
              if (type !== "object") throw new TypeErrorCtor("Value is not losslessly JSON encodable");
              if (seen.indexOf(item) >= 0) throw new TypeErrorCtor("Circular JSON value");
              const array = isArray(item);
              if (!array && prototype(item) !== plain && prototype(item) !== null) throw new TypeErrorCtor("Expected a plain JSON object");
              seen.push(item);
              const keys = ownKeys(item), pieces = [];
              if (array) {
                if (keys.length !== item.length + 1) throw new TypeErrorCtor("Sparse or extended arrays are not JSON");
                for (let i = 0; i < item.length; i++) {
                  const d = descriptor(item, "" + i);
                  if (!d || !has(d, "value")) throw new TypeErrorCtor("JSON accessors are not allowed");
                  pieces.push(visit(d.value, depth + 1));
                }
              } else {
                for (const key of keys) {
                  const d = descriptor(item, key);
                  if (typeof key !== "string" || !d.enumerable || !has(d, "value")) throw new TypeErrorCtor("JSON accessors or hidden fields are not allowed");
                  pieces.push(add(quote(key) + ":") + visit(d.value, depth + 1));
                }
              }
              seen.pop();
              return add(array ? "[" : "{") + pieces.join(add(",")) + add(array ? "]" : "}");
            }
            return visit(value, 0);
          }
          const tools = freeze({
            list: () => catalog.map(item => ({ name: item.name, description: item.description.slice(0, 240) })),
            describe: name => {
              const found = catalog.find(item => item.name === name);
              return found ? parse(quote(found)) : null;
            },
            call: async (name, args) => {
              let encoded;
              try { encoded = encode(args, argsLimit); } catch (_) { encoded = ""; }
              return parse(await call(typeof name === "string" ? name : "", encoded));
            }
          });
          const text = value => {
            let encoded;
            try { encoded = encode(value, outputLimit); } catch (_) { encoded = ""; }
            // Even a caught encoding/size error must enter Core's irreversible output-stop gate.
            emit(encoded);
          };
          return program(tools, text, inputs);
        })(
        """;
}
