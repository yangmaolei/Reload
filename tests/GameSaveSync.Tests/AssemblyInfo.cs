using Xunit;

// 这套测试大量操作真实文件系统与进程内 WebDAV 服务器，
// 并行会互相干扰（尤其 A1 的 5000 文件吞吐测量）。串行跑，结果才稳定可复现。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
