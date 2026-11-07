# P5S Unicode Input 1.1.0

Windows x64 输入组件源码，对应发布包 Core/components/P5SUnicodeInput.asi。

编译依赖 MinHook v1.3.4 和 Windows SDK。将 P5SUnicodeInput.c 与 MinHook 的 buffer.c、hook.c、trampoline.c、hde/hde64.c 静态链接成 DLL，输出文件名为 P5SUnicodeInput.asi。

在 Visual Studio x64 Native Tools 命令行中设置 MINHOOK_ROOT 为 MinHook 源码目录后，可执行以下命令。

```bat
cl /LD /O2 /I "%MINHOOK_ROOT%\include" P5SUnicodeInput.c "%MINHOOK_ROOT%\src\buffer.c" "%MINHOOK_ROOT%\src\hook.c" "%MINHOOK_ROOT%\src\trampoline.c" "%MINHOOK_ROOT%\src\hde\hde64.c" /link /OUT:P5SUnicodeInput.asi
```

input-coverage.h 根据两套发布字库的共同汉字映射生成，对应校验值见 input-coverage.json。组件校验游戏所需代码位置后启用，运行期处理 IME 编码和汉字有效性。

发布二进制 SHA-256 为 `53a800a112f2f9f6dbde6434064c331a2117c1c7fbf4467e82d4d4a13d0cde9b`。不同编译器生成的二进制校验值可能不同。

MinHook 源码与许可见 [MinHook v1.3.4](https://github.com/TsudaKageyu/minhook/tree/v1.3.4)。

自动加载器及其源码见 [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader)。
