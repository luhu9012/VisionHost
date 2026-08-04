using System;

namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 通用无返回值执行结果
    /// 全系统统一用该对象承载方法调用成功/失败、错误信息、异常
    /// </summary>
    public class Result
    {
        /// <summary>执行是否成功</summary>
        public bool Success { get; set; }

        /// <summary>自定义错误码，0=正常，负数为各类故障</summary>
        public int ErrorCode { get; set; }

        /// <summary>可读中文消息，用于日志与界面提示</summary>
        public string Message { get; set; }

        /// <summary>原始异常对象，用于日志详细排查</summary>
        public Exception Exception { get; set; }

        /// <summary>快捷构建成功结果</summary>
        public static Result Ok()
        {
            return new Result { Success = true, ErrorCode = 0, Message = "执行正常" };
        }

        /// <summary>快捷构建失败结果</summary>
        /// <param name="msg">失败说明</param>
        /// <param name="code">错误编号</param>
        /// <param name="ex">捕获的异常</param>
        public static Result Fail(string msg, int code = -1, Exception ex = null)
        {
            return new Result
            {
                Success = false,
                Message = msg,
                ErrorCode = code,
                Exception = ex
            };
        }
    }

    /// <summary>带泛型返回数据的结果封装</summary>
    /// <typeparam name="T">返回承载的数据类型</typeparam>
    public class Result<T> : Result
    {
        /// <summary>成功时返回的业务数据</summary>
        public T Data { get; set; }

        /// <summary>带返回数据的成功构建</summary>
        public static Result<T> Ok(T data)
        {
            return new Result<T>
            {
                Success = true,
                ErrorCode = 0,
                Message = "执行正常",
                Data = data
            };
        }

        /// <summary>带返回数据的失败构建</summary>
        public new static Result<T> Fail(string msg, int code = -1, Exception ex = null)
        {
            return new Result<T>
            {
                Success = false,
                Message = msg,
                ErrorCode = code,
                Exception = ex
            };
        }
    }
}